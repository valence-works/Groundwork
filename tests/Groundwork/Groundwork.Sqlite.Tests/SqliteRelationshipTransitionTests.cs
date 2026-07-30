using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.Capabilities;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Documents.Scoping;
using Groundwork.Sqlite.Documents;
using Groundwork.Sqlite.PhysicalStorage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteRelationshipTransitionTests
{
    private static readonly byte[] DiagnosticKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public async Task Valid_candidate_backfills_exact_sidecars_and_fences_then_activates_once()
    {
        await using var database = TemporaryDatabase.Create();
        var plan = CreatePlan("valid");
        var executor = CreateExecutor(database.ConnectionString, plan);
        var authorization = plan.ProjectReferenceIdentity("authorization-a")!.Value;

        var result = await executor.ExecuteAsync(
        [
            Source("token-a", "authorization-a"),
            Source("token-b", "authorization-a"),
            Source("token-c", null)
        ],
        [Target(authorization)]);

        Assert.Equal(SqliteRelationshipTransitionStatus.Activated, result.Status);
        var snapshot = await executor.InspectForTestOnlyAsync();
        Assert.Equal(plan.MaterializationSchema.GenerationIdentity, snapshot.ActiveGeneration);
        Assert.Equal(SqliteRelationshipTransitionPhase.Active, snapshot.CandidatePhase);
        Assert.Equal(3, snapshot.ProcessedSourceCount);
        Assert.Equal(2, snapshot.ReferenceCount);
        Assert.Equal(1, snapshot.FenceCount);

        var replay = await CreateExecutor(database.ConnectionString, plan).ExecuteAsync(
            [Source("token-a", "authorization-a"), Source("token-b", "authorization-a"), Source("token-c", null)],
            [Target(authorization)]);
        Assert.Equal(SqliteRelationshipTransitionStatus.Activated, replay.Status);
    }

    [Fact]
    public async Task Dangling_candidate_is_privately_rejected_and_reopen_replays_the_same_candidate_bound_diagnostic()
    {
        await using var database = TemporaryDatabase.Create();
        var plan = CreatePlan("dangling");
        var source = Source("token-missing", "missing-target-value");
        var first = await CreateExecutor(database.ConnectionString, plan).ExecuteAsync([source], []);

        Assert.Equal(SqliteRelationshipTransitionStatus.DanglingReference, first.Status);
        var diagnostic = Assert.IsType<RelationshipMaterializationDanglingReference>(first.DanglingReference);
        Assert.Equal("GW-RELATIONSHIP-013", RelationshipMaterializationDanglingReference.DiagnosticCode);
        Assert.DoesNotContain("tenant-a", diagnostic.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("missing-target-value", diagnostic.CanonicalJson, StringComparison.Ordinal);
        var snapshot = await CreateExecutor(database.ConnectionString, plan).InspectForTestOnlyAsync();
        Assert.Null(snapshot.ActiveGeneration);
        Assert.Equal(SqliteRelationshipTransitionPhase.Failed, snapshot.CandidatePhase);
        Assert.Equal(0, snapshot.ReferenceCount);
        Assert.Equal(0, snapshot.FenceCount);

        var replay = await CreateExecutor(database.ConnectionString, plan).ExecuteAsync([source], []);
        Assert.Equal(SqliteRelationshipTransitionStatus.DanglingReference, replay.Status);
        Assert.Equal(diagnostic.TargetKeyCorrelationIdentity, replay.DanglingReference!.TargetKeyCorrelationIdentity);
    }

    [Fact]
    public async Task Cancellation_persists_bounded_progress_and_a_distinct_instance_resumes_safely()
    {
        await using var database = TemporaryDatabase.Create();
        var plan = CreatePlan("reopen");
        var authorizationA = plan.ProjectReferenceIdentity("authorization-a")!.Value;
        var authorizationB = plan.ProjectReferenceIdentity("authorization-b")!.Value;
        var sources = new[] { Source("token-a", "authorization-a"), Source("token-b", "authorization-b") };
        var targets = new[] { Target(authorizationA), Target(authorizationB) };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateExecutor(database.ConnectionString, plan).ExecuteAsync(
                sources,
                targets,
                new SqliteRelationshipTransitionTestOptions(false, CancelAfterProcessedSourceCount: 1)));

        var interrupted = await CreateExecutor(database.ConnectionString, plan).InspectForTestOnlyAsync();
        Assert.Null(interrupted.ActiveGeneration);
        Assert.Equal(SqliteRelationshipTransitionPhase.Preparing, interrupted.CandidatePhase);
        Assert.Equal(1, interrupted.ProcessedSourceCount);

        var resumed = await CreateExecutor(database.ConnectionString, plan).ExecuteAsync(sources, targets);
        Assert.Equal(SqliteRelationshipTransitionStatus.Activated, resumed.Status);
        var completed = await CreateExecutor(database.ConnectionString, plan).InspectForTestOnlyAsync();
        Assert.Equal(SqliteRelationshipTransitionPhase.Active, completed.CandidatePhase);
        Assert.Equal(2, completed.ProcessedSourceCount);
    }

    [Fact]
    public async Task Same_candidate_lost_acknowledgement_converges()
    {
        await using var database = TemporaryDatabase.Create();
        var firstPlan = CreatePlan("candidate-a");
        var source = Source("token-a", "authorization-a");
        var target = Target(firstPlan.ProjectReferenceIdentity("authorization-a")!.Value);

        await Assert.ThrowsAsync<SqliteRelationshipTransitionAcknowledgementLostException>(() =>
            CreateExecutor(database.ConnectionString, firstPlan).ExecuteAsync(
                [source],
                [target],
                new SqliteRelationshipTransitionTestOptions(true)));

        var retry = await CreateExecutor(database.ConnectionString, firstPlan).ExecuteAsync([source], [target]);
        Assert.Equal(SqliteRelationshipTransitionStatus.Activated, retry.Status);
        var snapshot = await CreateExecutor(database.ConnectionString, firstPlan).InspectForTestOnlyAsync();
        Assert.Equal(firstPlan.MaterializationSchema.GenerationIdentity, snapshot.ActiveGeneration);
    }

    [Fact]
    public async Task Validation_acknowledgement_loss_reopens_from_the_validated_non_authoritative_candidate()
    {
        await using var database = TemporaryDatabase.Create();
        var plan = CreatePlan("validated-reopen");
        var source = Source("token-a", "authorization-a");
        var target = Target(plan.ProjectReferenceIdentity("authorization-a")!.Value);

        await Assert.ThrowsAsync<SqliteRelationshipTransitionValidationAcknowledgementLostException>(() =>
            CreateExecutor(database.ConnectionString, plan).ExecuteAsync(
                [source],
                [target],
                new SqliteRelationshipTransitionTestOptions(false, ThrowAfterValidationCommit: true)));

        var interrupted = await CreateExecutor(database.ConnectionString, plan).InspectForTestOnlyAsync();
        Assert.Null(interrupted.ActiveGeneration);
        Assert.Equal(SqliteRelationshipTransitionPhase.Validated, interrupted.CandidatePhase);
        var resumed = await CreateExecutor(database.ConnectionString, plan).ExecuteAsync([source], [target]);
        Assert.Equal(SqliteRelationshipTransitionStatus.Activated, resumed.Status);
    }

    [Fact]
    public async Task Competing_expected_absent_candidates_serialize_to_one_authoritative_generation()
    {
        await using var database = TemporaryDatabase.Create();
        var firstPlan = CreatePlan("competing-a");
        var secondPlan = CreatePlan("competing-b");
        var source = Source("token-a", "authorization-a");
        var target = Target(firstPlan.ProjectReferenceIdentity("authorization-a")!.Value);

        var results = await Task.WhenAll(
            Task.Run(() => CreateExecutor(database.ConnectionString, firstPlan).ExecuteAsync([source], [target])),
            Task.Run(() => CreateExecutor(database.ConnectionString, secondPlan).ExecuteAsync([source], [target])));

        Assert.Single(results.Where(result => result.Status == SqliteRelationshipTransitionStatus.Activated));
        Assert.Single(results.Where(result => result.Status == SqliteRelationshipTransitionStatus.RelationshipConflict));
        var snapshot = await CreateExecutor(database.ConnectionString, firstPlan).InspectForTestOnlyAsync();
        Assert.True(new[]
            {
                firstPlan.MaterializationSchema.GenerationIdentity,
                secondPlan.MaterializationSchema.GenerationIdentity
            }
            .Contains(snapshot.ActiveGeneration));
    }

    [Fact]
    public async Task Rotation_requires_the_exact_active_generation_and_rejects_a_stale_expected_generation()
    {
        await using var database = TemporaryDatabase.Create();
        var firstPlan = CreatePlan("rotation-a");
        var secondPlan = CreatePlan("rotation-b");
        var thirdPlan = CreatePlan("rotation-c");
        var source = Source("token-a", "authorization-a");
        var target = Target(firstPlan.ProjectReferenceIdentity("authorization-a")!.Value);
        var firstGeneration = new RelationshipMaterializationGeneration(firstPlan.MaterializationSchema);

        Assert.Equal(
            SqliteRelationshipTransitionStatus.Activated,
            (await CreateExecutor(database.ConnectionString, firstPlan).ExecuteAsync([source], [target])).Status);
        var rotated = await CreateExecutor(
                database.ConnectionString,
                secondPlan,
                RelationshipMaterializationExpectedActive.Exact(firstGeneration))
            .ExecuteAsync([source], [target]);
        Assert.Equal(SqliteRelationshipTransitionStatus.Activated, rotated.Status);
        var stale = await CreateExecutor(
                database.ConnectionString,
                thirdPlan,
                RelationshipMaterializationExpectedActive.Exact(firstGeneration))
            .ExecuteAsync([source], [target]);
        Assert.Equal(SqliteRelationshipTransitionStatus.RelationshipConflict, stale.Status);
        var snapshot = await CreateExecutor(database.ConnectionString, secondPlan).InspectForTestOnlyAsync();
        Assert.Equal(secondPlan.MaterializationSchema.GenerationIdentity, snapshot.ActiveGeneration);
    }

    [Fact]
    public async Task Public_factory_still_rejects_relationship_manifest_before_it_opens_the_connection()
    {
        var manifest = CreateManifest("public-gate");
        await using var connection = new SqliteConnection("Data Source=:memory:");

        var exception = await Assert.ThrowsAsync<PhysicalRelationshipProviderNotSupportedException>(() =>
            SqliteDocumentStoreFactory.CreateAsync(
                connection,
                manifest,
                new ProviderIdentity("sqlite", "test"),
                DocumentStoreAccess.Global));

        Assert.Contains("GW-RELATIONSHIP-012", exception.Message, StringComparison.Ordinal);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    private static SqliteRelationshipTransitionExecutor CreateExecutor(
        string connectionString,
        PhysicalRelationshipPlan plan,
        RelationshipMaterializationExpectedActive? expectedActive = null) =>
        SqliteRelationshipTransitionExecutor.CreateForTestOnly(
            connectionString,
            plan,
            new RelationshipMaterializationTransitionRequirement(
                expectedActive ?? RelationshipMaterializationExpectedActive.Absent,
                new RelationshipMaterializationGeneration(plan.MaterializationSchema)),
            DiagnosticKey);

    private static SqliteRelationshipTransitionSourceRecord Source(string id, string? reference) =>
        new("tenant-a", $"lookup:{id}", $"comparison:{id}", "tenant-a", reference);

    private static SqliteRelationshipTransitionTargetRecord Target(Groundwork.Core.Text.PortableStringIdentityProjection identity) =>
        new("tenant-a", identity.LookupKey, identity.ComparisonKey);

    private static PhysicalRelationshipPlan CreatePlan(string suffix)
    {
        var manifest = CreateManifest(suffix);
        var routes = ManifestExecutableRouteSetCompiler.Compile(
            manifest,
            PhysicalNamePolicy.Identity,
            SqliteGroundworkCapabilities.PhysicalNames);
        Assert.True(routes.IsValid, string.Join("; ", routes.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var plans = PhysicalRelationshipPlanCompiler.Compile(Assert.IsType<ManifestExecutableRouteSet>(routes.RouteSet));
        Assert.True(plans.IsValid, string.Join("; ", plans.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return Assert.Single(plans.Plans);
    }

    private static StorageManifest CreateManifest(string suffix)
    {
        var authorizationId = new LogicalIndexDeclaration(
            "authorization-by-id",
            [new IndexField(PhysicalDocumentFieldPaths.Id)],
            IndexValueKind.Keyword,
            true,
            MissingValueBehavior.Excluded);
        var tokenAuthorization = new LogicalIndexDeclaration(
            "token-by-authorization-id",
            [new IndexField("authorizationId")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var authorization = StorageUnit.Create(
            new StorageUnitIdentity("authorization"),
            "Authorization",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.PhysicalEntityTable(
                    "authorizations",
                    [new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String)],
                    indexes:
                    [
                        new PhysicalIndexDefinition(
                            authorizationId.Identity,
                            [
                                new PhysicalIndexColumnDefinition("storage_scope", 0),
                                new PhysicalIndexColumnDefinition("id_comparison_key", 1)
                            ],
                            isUnique: true)
                    ])),
                [authorizationId]));
        var token = StorageUnit.Create(
            new StorageUnitIdentity("token"),
            "Token",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.PhysicalEntityTable(
                    "tokens",
                    [
                        new ProjectedColumnDefinition("status", "status", PortablePhysicalType.String),
                        new ProjectedColumnDefinition("authorizationId", "authorizationId", PortablePhysicalType.String)
                    ],
                    indexes:
                    [
                        new PhysicalIndexDefinition(
                            tokenAuthorization.Identity,
                            [
                                new PhysicalIndexColumnDefinition("storage_scope", 0),
                                new PhysicalIndexColumnDefinition("authorizationId", 1)
                            ])
                    ])),
                [tokenAuthorization]));
        return new(
            new StorageManifestIdentity($"relationship-transition-{suffix}"),
            new StorageManifestOwner("groundwork-tests"),
            new StorageManifestVersion("1"),
            [authorization, token],
            new HashSet<string>(),
            [])
        {
            Relationships =
            [
                new ManifestRelationshipDeclaration(
                    "token-authorization",
                    token.Identity,
                    "authorizationId",
                    tokenAuthorization.Identity,
                    authorization.Identity,
                    PhysicalDocumentFieldPaths.Id,
                    authorizationId.Identity,
                    StringIdentityCasePolicy.Ordinal)
            ]
        };
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private TemporaryDatabase(string path) => Path = path;

        public string Path { get; }
        public string ConnectionString => new SqliteConnectionStringBuilder { DataSource = Path }.ConnectionString;

        public static TemporaryDatabase Create() => new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"groundwork-relationship-transition-{Guid.NewGuid():N}.db"));

        public ValueTask DisposeAsync()
        {
            File.Delete(Path);
            return ValueTask.CompletedTask;
        }
    }
}
