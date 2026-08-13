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
        Assert.Equal(
            [ExpectedReference(plan, "token-a", authorization), ExpectedReference(plan, "token-b", authorization)],
            snapshot.References);
        Assert.Equal([ExpectedFence(plan, authorization)], snapshot.Fences);

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
    public async Task Changed_source_set_after_cancellation_fails_closed_instead_of_resuming_by_progress_index()
    {
        await using var database = TemporaryDatabase.Create();
        var plan = CreatePlan("changed-source-set");
        var authorizationA = plan.ProjectReferenceIdentity("authorization-a")!.Value;
        var authorizationB = plan.ProjectReferenceIdentity("authorization-b")!.Value;
        var originalSources = new[] { Source("token-a", "authorization-a"), Source("token-b", "authorization-b") };
        var targets = new[] { Target(authorizationA), Target(authorizationB) };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateExecutor(database.ConnectionString, plan).ExecuteAsync(
                originalSources,
                targets,
                new SqliteRelationshipTransitionTestOptions(false, CancelAfterProcessedSourceCount: 1)));

        var replay = await CreateExecutor(database.ConnectionString, plan).ExecuteAsync(
            [Source("token-a", "authorization-a"), Source("token-c", "authorization-b")],
            targets);

        Assert.Equal(SqliteRelationshipTransitionStatus.RelationshipConflict, replay.Status);
        var snapshot = await CreateExecutor(database.ConnectionString, plan).InspectForTestOnlyAsync();
        Assert.Null(snapshot.ActiveGeneration);
        Assert.Equal(SqliteRelationshipTransitionPhase.Preparing, snapshot.CandidatePhase);
        Assert.Equal(1, snapshot.ProcessedSourceCount);
    }

    [Fact]
    public async Task Changed_serialized_reference_for_the_same_source_identity_after_cancellation_fails_closed()
    {
        await using var database = TemporaryDatabase.Create();
        var plan = CreatePlan("changed-source-reference");
        var authorizationA = plan.ProjectReferenceIdentity("authorization-a")!.Value;
        var authorizationB = plan.ProjectReferenceIdentity("authorization-b")!.Value;
        var source = Source("token-a", "authorization-a");
        var targets = new[] { Target(authorizationA), Target(authorizationB) };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateExecutor(database.ConnectionString, plan).ExecuteAsync(
                [source, Source("token-b", "authorization-b")],
                targets,
                new SqliteRelationshipTransitionTestOptions(false, CancelAfterProcessedSourceCount: 1)));

        var replay = await CreateExecutor(database.ConnectionString, plan).ExecuteAsync(
            [Source("token-a", "authorization-b"), Source("token-b", "authorization-b")],
            targets);

        Assert.Equal(SqliteRelationshipTransitionStatus.RelationshipConflict, replay.Status);
        var snapshot = await CreateExecutor(database.ConnectionString, plan).InspectForTestOnlyAsync();
        Assert.Null(snapshot.ActiveGeneration);
        Assert.Equal(1, snapshot.ProcessedSourceCount);
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
    public async Task Changed_source_and_target_after_validation_acknowledgement_loss_cannot_activate_the_candidate()
    {
        await using var database = TemporaryDatabase.Create();
        var plan = CreatePlan("validated-input-change");
        var authorizationA = plan.ProjectReferenceIdentity("authorization-a")!.Value;
        var authorizationB = plan.ProjectReferenceIdentity("authorization-b")!.Value;
        var source = Source("token-a", "authorization-a");

        await Assert.ThrowsAsync<SqliteRelationshipTransitionValidationAcknowledgementLostException>(() =>
            CreateExecutor(database.ConnectionString, plan).ExecuteAsync(
                [source],
                [Target(authorizationA)],
                new SqliteRelationshipTransitionTestOptions(false, ThrowAfterValidationCommit: true)));

        var replay = await CreateExecutor(database.ConnectionString, plan).ExecuteAsync(
            [Source("token-a", "authorization-b")],
            [Target(authorizationB)]);

        Assert.Equal(SqliteRelationshipTransitionStatus.RelationshipConflict, replay.Status);
        var snapshot = await CreateExecutor(database.ConnectionString, plan).InspectForTestOnlyAsync();
        Assert.Null(snapshot.ActiveGeneration);
        Assert.Equal(SqliteRelationshipTransitionPhase.Validated, snapshot.CandidatePhase);
    }

    [Fact]
    public async Task Failed_candidate_replays_its_persisted_diagnostic_when_the_current_input_omits_the_dangling_source_or_adds_its_target()
    {
        await using var database = TemporaryDatabase.Create();
        var plan = CreatePlan("persisted-failure");
        var authorizationA = plan.ProjectReferenceIdentity("authorization-a")!.Value;
        var authorizationB = plan.ProjectReferenceIdentity("authorization-b")!.Value;
        var valid = Source("token-a", "authorization-a");
        var dangling = Source("token-b", "authorization-b");

        var first = await CreateExecutor(database.ConnectionString, plan).ExecuteAsync([valid, dangling], [Target(authorizationA)]);
        var expected = Assert.IsType<RelationshipMaterializationDanglingReference>(first.DanglingReference);

        var replay = await CreateExecutor(database.ConnectionString, plan).ExecuteAsync(
            [valid],
            [Target(authorizationA), Target(authorizationB)]);

        Assert.Equal(SqliteRelationshipTransitionStatus.DanglingReference, replay.Status);
        var restored = Assert.IsType<RelationshipMaterializationDanglingReference>(replay.DanglingReference);
        Assert.Contains(RelationshipMaterializationDanglingReference.DiagnosticCode, restored.Message, StringComparison.Ordinal);
        Assert.Equal(expected.TargetKeyCorrelationIdentity, restored.TargetKeyCorrelationIdentity);
        Assert.Equal(expected.CanonicalJson, restored.CanonicalJson);
        var snapshot = await CreateExecutor(database.ConnectionString, plan).InspectForTestOnlyAsync();
        Assert.Null(snapshot.ActiveGeneration);
        Assert.Equal(SqliteRelationshipTransitionPhase.Failed, snapshot.CandidatePhase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_candidate_rejects_a_swapped_correlation_or_corrupt_failure_mac(bool corruptMac)
    {
        await using var firstDatabase = TemporaryDatabase.Create();
        await using var secondDatabase = TemporaryDatabase.Create();
        var firstPlan = CreatePlan("failure-mac-a");
        var secondPlan = CreatePlan("failure-mac-b");
        var source = Source("token-missing", "missing-target-value");

        Assert.Equal(
            SqliteRelationshipTransitionStatus.DanglingReference,
            (await CreateExecutor(firstDatabase.ConnectionString, firstPlan).ExecuteAsync([source], [])).Status);
        Assert.Equal(
            SqliteRelationshipTransitionStatus.DanglingReference,
            (await CreateExecutor(secondDatabase.ConnectionString, secondPlan).ExecuteAsync([source], [])).Status);

        if (corruptMac)
        {
            await ExecuteSqlAsync(
                firstDatabase.ConnectionString,
                "UPDATE groundwork_relationship_transition_state_v1 SET failure_mac = @value;",
                ("@value", $"hmac-sha256-v1:{new string('0', 64)}"));
        }
        else
        {
            var otherCorrelation = await ReadScalarAsync(
                secondDatabase.ConnectionString,
                "SELECT failure_correlation FROM groundwork_relationship_transition_state_v1;");
            var otherMac = await ReadScalarAsync(
                secondDatabase.ConnectionString,
                "SELECT failure_mac FROM groundwork_relationship_transition_state_v1;");
            await ExecuteSqlAsync(
                firstDatabase.ConnectionString,
                "UPDATE groundwork_relationship_transition_state_v1 SET failure_correlation = @correlation, failure_mac = @mac;",
                ("@correlation", otherCorrelation),
                ("@mac", otherMac));
        }

        var replay = await CreateExecutor(firstDatabase.ConnectionString, firstPlan).ExecuteAsync([source], []);
        Assert.Equal(SqliteRelationshipTransitionStatus.RelationshipConflict, replay.Status);
        Assert.Null(replay.DanglingReference);
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
        Assert.Equal([ExpectedReference(secondPlan, "token-a", secondPlan.ProjectReferenceIdentity("authorization-a")!.Value)], snapshot.References);
        Assert.Equal([ExpectedFence(secondPlan, secondPlan.ProjectReferenceIdentity("authorization-a")!.Value)], snapshot.Fences);

        var priorCandidate = await CreateExecutor(database.ConnectionString, firstPlan).InspectForTestOnlyAsync();
        Assert.Equal(secondPlan.MaterializationSchema.GenerationIdentity, priorCandidate.ActiveGeneration);
        Assert.Equal([ExpectedReference(firstPlan, "token-a", firstPlan.ProjectReferenceIdentity("authorization-a")!.Value)], priorCandidate.References);
        Assert.Equal([ExpectedFence(firstPlan, firstPlan.ProjectReferenceIdentity("authorization-a")!.Value)], priorCandidate.Fences);
        Assert.NotEqual(
            priorCandidate.References[0].CandidateGeneration,
            snapshot.References[0].CandidateGeneration);
    }

    [Theory]
    [InlineData("DELETE FROM groundwork_relationship_transition_state_v1;")]
    [InlineData("UPDATE groundwork_relationship_transition_state_v1 SET phase = 1;")]
    [InlineData("UPDATE groundwork_relationship_transition_state_v1 SET candidate_generation = candidate_generation || '-corrupt';")]
    [InlineData("DELETE FROM groundwork_relationship_reference_sidecar_v1;")]
    [InlineData("UPDATE groundwork_relationship_reference_sidecar_v1 SET candidate_generation = candidate_generation || '-corrupt';")]
    [InlineData("DELETE FROM groundwork_relationship_target_fence_v1;")]
    [InlineData("UPDATE groundwork_relationship_target_fence_v1 SET candidate_generation = candidate_generation || '-corrupt';")]
    [InlineData("UPDATE groundwork_relationship_active_v1 SET materialization_fingerprint = materialization_fingerprint || '-corrupt';")]
    public async Task Active_candidate_reopen_fails_closed_when_durable_evidence_is_missing_or_corrupt(string corruption)
    {
        await using var database = TemporaryDatabase.Create();
        var plan = CreatePlan($"active-corruption-{Guid.NewGuid():N}");
        var source = Source("token-a", "authorization-a");
        var target = Target(plan.ProjectReferenceIdentity("authorization-a")!.Value);
        var executor = CreateExecutor(database.ConnectionString, plan);

        Assert.Equal(
            SqliteRelationshipTransitionStatus.Activated,
            (await executor.ExecuteAsync([source], [target])).Status);
        await ExecuteSqlAsync(database.ConnectionString, corruption);

        var replay = await CreateExecutor(database.ConnectionString, plan).ExecuteAsync([source], [target]);
        Assert.Equal(SqliteRelationshipTransitionStatus.RelationshipConflict, replay.Status);
    }

    [Fact]
    public async Task Pre_failure_mac_v1_state_schema_is_upgraded_before_reopen()
    {
        await using var database = TemporaryDatabase.Create();
        await ExecuteSqlAsync(
            database.ConnectionString,
            """
            CREATE TABLE groundwork_relationship_transition_state_v1 (
                relationship_identity TEXT NOT NULL,
                candidate_generation TEXT NOT NULL,
                candidate_fingerprint TEXT NOT NULL,
                expected_kind INTEGER NOT NULL,
                expected_generation TEXT NULL,
                expected_fingerprint TEXT NULL,
                candidate_input_digest TEXT NOT NULL,
                phase INTEGER NOT NULL,
                processed_source_count INTEGER NOT NULL,
                failure_code TEXT NULL,
                failure_correlation TEXT NULL,
                PRIMARY KEY (relationship_identity, candidate_generation, candidate_fingerprint)
            );
            """);
        var plan = CreatePlan("legacy-state-schema");
        var source = Source("token-a", "authorization-a");
        var target = Target(plan.ProjectReferenceIdentity("authorization-a")!.Value);

        var result = await CreateExecutor(database.ConnectionString, plan).ExecuteAsync([source], [target]);

        Assert.Equal(SqliteRelationshipTransitionStatus.Activated, result.Status);
        Assert.Equal(
            1,
            await ReadInt64Async(
                database.ConnectionString,
                "SELECT COUNT(*) FROM pragma_table_info('groundwork_relationship_transition_state_v1') WHERE name = 'failure_mac';"));
    }

    [Fact]
    public async Task Pre_failure_mac_v1_state_with_a_hidden_column_is_not_upgraded()
    {
        await using var database = TemporaryDatabase.Create();
        await ExecuteSqlAsync(
            database.ConnectionString,
            """
            CREATE TABLE groundwork_relationship_transition_state_v1 (
                relationship_identity TEXT NOT NULL,
                candidate_generation TEXT NOT NULL,
                candidate_fingerprint TEXT NOT NULL,
                expected_kind INTEGER NOT NULL,
                expected_generation TEXT NULL,
                expected_fingerprint TEXT NULL,
                candidate_input_digest TEXT NOT NULL,
                phase INTEGER NOT NULL,
                processed_source_count INTEGER NOT NULL,
                failure_code TEXT NULL,
                failure_correlation TEXT NULL,
                unexpected_shadow TEXT GENERATED ALWAYS AS (relationship_identity) VIRTUAL,
                PRIMARY KEY (relationship_identity, candidate_generation, candidate_fingerprint)
            );
            """);
        var plan = CreatePlan("legacy-hidden-state-schema");
        var source = Source("token-a", "authorization-a");
        var target = Target(plan.ProjectReferenceIdentity("authorization-a")!.Value);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateExecutor(database.ConnectionString, plan).ExecuteAsync([source], [target]));

        Assert.Contains("groundwork_relationship_transition_state_v1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Active_candidate_reopen_rejects_a_duplicate_capable_sidecar_schema()
    {
        await using var database = TemporaryDatabase.Create();
        var plan = CreatePlan("malformed-sidecar-schema");
        var source = Source("token-a", "authorization-a");
        var target = Target(plan.ProjectReferenceIdentity("authorization-a")!.Value);
        var executor = CreateExecutor(database.ConnectionString, plan);
        Assert.Equal(
            SqliteRelationshipTransitionStatus.Activated,
            (await executor.ExecuteAsync([source], [target])).Status);
        await ExecuteSqlAsync(
            database.ConnectionString,
            """
            ALTER TABLE groundwork_relationship_reference_sidecar_v1
                RENAME TO groundwork_relationship_reference_sidecar_legacy;
            CREATE TABLE groundwork_relationship_reference_sidecar_v1 (
                relationship_identity TEXT NOT NULL,
                candidate_generation TEXT NOT NULL,
                candidate_fingerprint TEXT NOT NULL,
                source_scope TEXT NOT NULL,
                source_lookup_key TEXT NOT NULL,
                source_comparison_key TEXT NOT NULL,
                target_scope TEXT NOT NULL,
                target_lookup_key TEXT NOT NULL,
                target_comparison_key TEXT NOT NULL
            );
            INSERT INTO groundwork_relationship_reference_sidecar_v1
            SELECT * FROM groundwork_relationship_reference_sidecar_legacy;
            INSERT INTO groundwork_relationship_reference_sidecar_v1
            SELECT * FROM groundwork_relationship_reference_sidecar_legacy;
            """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateExecutor(database.ConnectionString, plan).ExecuteAsync([source], [target]));

        Assert.Contains("groundwork_relationship_reference_sidecar_v1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Active_candidate_reopen_rejects_a_case_insensitive_primary_key()
    {
        await using var database = TemporaryDatabase.Create();
        var plan = CreatePlan("malformed-sidecar-collation");
        var source = Source("token-a", "authorization-a");
        var target = Target(plan.ProjectReferenceIdentity("authorization-a")!.Value);
        var executor = CreateExecutor(database.ConnectionString, plan);
        Assert.Equal(
            SqliteRelationshipTransitionStatus.Activated,
            (await executor.ExecuteAsync([source], [target])).Status);
        await ExecuteSqlAsync(
            database.ConnectionString,
            """
            ALTER TABLE groundwork_relationship_reference_sidecar_v1
                RENAME TO groundwork_relationship_reference_sidecar_legacy;
            CREATE TABLE groundwork_relationship_reference_sidecar_v1 (
                relationship_identity TEXT NOT NULL,
                candidate_generation TEXT NOT NULL,
                candidate_fingerprint TEXT NOT NULL,
                source_scope TEXT COLLATE NOCASE NOT NULL,
                source_lookup_key TEXT NOT NULL,
                source_comparison_key TEXT NOT NULL,
                target_scope TEXT NOT NULL,
                target_lookup_key TEXT NOT NULL,
                target_comparison_key TEXT NOT NULL,
                PRIMARY KEY (
                    relationship_identity, candidate_generation, candidate_fingerprint,
                    source_scope, source_lookup_key, source_comparison_key)
            );
            INSERT INTO groundwork_relationship_reference_sidecar_v1
            SELECT * FROM groundwork_relationship_reference_sidecar_legacy;
            """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateExecutor(database.ConnectionString, plan).ExecuteAsync([source], [target]));

        Assert.Contains("ordinal text-column schema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Active_candidate_reopen_rejects_case_insensitive_non_key_identity_columns()
    {
        await using var database = TemporaryDatabase.Create();
        var plan = CreatePlan("malformed-active-collation");
        var source = Source("token-a", "authorization-a");
        var target = Target(plan.ProjectReferenceIdentity("authorization-a")!.Value);
        var executor = CreateExecutor(database.ConnectionString, plan);
        Assert.Equal(
            SqliteRelationshipTransitionStatus.Activated,
            (await executor.ExecuteAsync([source], [target])).Status);
        await ExecuteSqlAsync(
            database.ConnectionString,
            """
            ALTER TABLE groundwork_relationship_active_v1
                RENAME TO groundwork_relationship_active_legacy;
            CREATE TABLE groundwork_relationship_active_v1 (
                relationship_identity TEXT NOT NULL PRIMARY KEY,
                generation_identity TEXT COLLATE NOCASE NOT NULL,
                materialization_fingerprint TEXT COLLATE NOCASE NOT NULL
            );
            INSERT INTO groundwork_relationship_active_v1
            SELECT * FROM groundwork_relationship_active_legacy;
            """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateExecutor(database.ConnectionString, plan).ExecuteAsync([source], [target]));

        Assert.Contains("ordinal text-column schema", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_candidate_reopen_rejects_a_non_ordinal_or_descending_target_index(bool descending)
    {
        await using var database = TemporaryDatabase.Create();
        var plan = CreatePlan($"malformed-target-index-{descending}");
        var source = Source("token-a", "authorization-a");
        var target = Target(plan.ProjectReferenceIdentity("authorization-a")!.Value);
        var executor = CreateExecutor(database.ConnectionString, plan);
        Assert.Equal(
            SqliteRelationshipTransitionStatus.Activated,
            (await executor.ExecuteAsync([source], [target])).Status);
        var targetColumns = descending
            ? "target_scope, target_lookup_key, target_comparison_key DESC"
            : "target_scope COLLATE NOCASE, target_lookup_key, target_comparison_key";
        await ExecuteSqlAsync(
            database.ConnectionString,
            $"""
            DROP INDEX ix_groundwork_relationship_reference_sidecar_v1_target;
            CREATE INDEX ix_groundwork_relationship_reference_sidecar_v1_target
                ON groundwork_relationship_reference_sidecar_v1 (
                    relationship_identity, candidate_generation, candidate_fingerprint,
                    {targetColumns});
            """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateExecutor(database.ConnectionString, plan).ExecuteAsync([source], [target]));

        Assert.Contains("ix_groundwork_relationship_reference_sidecar_v1_target", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_factory_still_rejects_relationship_manifest_before_it_opens_the_connection()
    {
        var manifest = CreateManifest("public-gate");
        await using var connection = new SqliteConnection("Data Source=:memory:");

        var exception = await Assert.ThrowsAsync<PhysicalRelationshipProviderNotSupportedException>(() =>
            SqliteDocumentStoreFactory.OpenPhysicalAsync(
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

    private static SqliteRelationshipTransitionReferenceSnapshot ExpectedReference(
        PhysicalRelationshipPlan plan,
        string sourceId,
        Groundwork.Core.Text.PortableStringIdentityProjection target) =>
        new(
            plan.MaterializationSchema.RelationshipIdentity,
            plan.MaterializationSchema.GenerationIdentity,
            plan.MaterializationSchema.Fingerprint,
            "tenant-a",
            $"lookup:{sourceId}",
            $"comparison:{sourceId}",
            "tenant-a",
            target.LookupKey,
            target.ComparisonKey);

    private static SqliteRelationshipTransitionFenceSnapshot ExpectedFence(
        PhysicalRelationshipPlan plan,
        Groundwork.Core.Text.PortableStringIdentityProjection target) =>
        new(
            plan.MaterializationSchema.RelationshipIdentity,
            plan.MaterializationSchema.GenerationIdentity,
            plan.MaterializationSchema.Fingerprint,
            "tenant-a",
            target.LookupKey,
            target.ComparisonKey);

    private static async Task ExecuteSqlAsync(
        string connectionString,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadScalarAsync(string connectionString, string commandText)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<long> ReadInt64Async(string connectionString, string commandText)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }

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
            MissingValueBehavior.IncludedAsNull);
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
                            isUnique: true,
                    missingValueBehavior: MissingValueBehavior.Excluded)
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
