using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.Materialization;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Queries;
using Groundwork.Core.Validation;
using Xunit;

namespace Groundwork.Materialization.Tests;

public sealed class MaterializationPlannerTests
{
    private readonly ProviderIdentity provider = new("materialization-test-provider", "1.0.0");
    private readonly MaterializationPlanner planner = new(new StorageManifestValidator(), new ProviderCapabilityValidator());

    [Fact]
    public void PlanCreatesTypedOperationsAndSchemaHistoryForPlannableManifest()
    {
        var manifest = CreateManifest();
        var runtimeCapabilities = RuntimeCapabilities();
        var materializationCapabilities = CreateCapabilities();

        var plan = planner.Plan(manifest, runtimeCapabilities, materializationCapabilities);

        Assert.True(plan.IsPlannable);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(provider, plan.Provider);
        Assert.Equal(manifest.Identity, plan.ManifestIdentity);
        Assert.Equal(manifest.Version, plan.ManifestVersion);
        Assert.DoesNotContain(typeof(StorageManifest), typeof(MaterializationPlan).GetProperties().Select(property => property.PropertyType));
        Assert.Collection(
            plan.Operations,
            operation => Assert.IsType<CreateStorageUnitOperation>(operation),
            operation => Assert.IsType<CreateIndexOperation>(operation),
            operation => Assert.IsType<BackfillCanonicalJsonOperation>(operation),
            operation => Assert.IsType<CreateIndexOperation>(operation),
            operation => Assert.IsType<BackfillCanonicalJsonOperation>(operation),
            operation => Assert.IsType<CreateOptimizedProjectionOperation>(operation),
            operation => Assert.IsType<RecordSchemaHistoryOperation>(operation));

        var historyOperation = Assert.IsType<RecordSchemaHistoryOperation>(plan.Operations[^1]);
        Assert.Same(plan.SchemaHistory, historyOperation.Entry);
        Assert.Equal(provider, plan.SchemaHistory.Provider);
        Assert.Equal(manifest.Identity, plan.SchemaHistory.ManifestIdentity);
        Assert.Equal(manifest.Version, plan.SchemaHistory.ManifestVersion);
        Assert.Equal(
            [
                "configurationDocument",
                "configurationDocument.by-key",
                "configurationDocument.by-key.backfill-canonical-json",
                "configurationDocument.by-category",
                "configurationDocument.by-category.backfill-canonical-json",
                "configurationDocument.optimized-projection"
            ],
            plan.SchemaHistory.AppliedOperationTargets);
        var backfills = plan.Operations
            .OfType<Groundwork.Core.SchemaEvolution.BackfillCanonicalJsonOperation>()
            .ToArray();
        Assert.Equal(2, backfills.Length);
        Assert.All(backfills, backfill =>
        {
            Assert.Equal(CanonicalJsonBackfillSubjectKind.LogicalIndex, backfill.SubjectKind);
            Assert.NotNull(backfill.LogicalIndex);
            Assert.Null(backfill.Route);
        });
        Assert.DoesNotContain(
            plan.Operations,
            operation => operation.GetType().Namespace == "Groundwork.Materialization" &&
                         operation.GetType().Name == "BackfillCanonicalJsonOperation");
    }

    [Fact]
    public void PlanReturnsDiagnosticsAndNoOperationsWhenSchemaHistoryIsUnsupported()
    {
        var manifest = CreateManifest();
        var materializationCapabilities = CreateCapabilities(supportsSchemaHistory: false);

        var plan = planner.Plan(manifest, RuntimeCapabilities(), materializationCapabilities);

        Assert.False(plan.IsPlannable);
        Assert.Empty(plan.Operations);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "GW-MAT-001");
    }

    [Fact]
    public void PlanReturnsDiagnosticsAndNoOperationsWhenMaterializationOperationIsUnsupported()
    {
        var manifest = CreateManifest();
        var supportedOperations = Enum.GetValues<MaterializationOperationKind>()
            .Where(operation => operation != MaterializationOperationKind.CreateOptimizedProjection)
            .ToHashSet();
        var materializationCapabilities = CreateCapabilities(supportedOperations: supportedOperations);

        var plan = planner.Plan(manifest, RuntimeCapabilities(), materializationCapabilities);

        Assert.False(plan.IsPlannable);
        Assert.Empty(plan.Operations);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "GW-MAT-002");
    }

    [Fact]
    public void PlanReturnsDiagnosticsAndNoOperationsWhenCapabilityProvidersDiffer()
    {
        var manifest = CreateManifest();
        var runtimeCapabilities = RuntimeCapabilities();
        var materializationCapabilities = CreateCapabilities(provider: new ProviderIdentity("other-provider", "2.0.0"));

        var plan = planner.Plan(manifest, runtimeCapabilities, materializationCapabilities);

        Assert.False(plan.IsPlannable);
        Assert.Empty(plan.Operations);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "GW-MAT-003");
    }

    [Fact]
    public void PlanRejectsRelationshipManifestsUntilTheProviderAdvertisesMaterializationAndFenceSupport()
    {
        var manifest = CreateManifest();
        var unit = Assert.Single(manifest.StorageUnits);
        manifest = manifest with
        {
            Relationships =
            [
                new ManifestRelationshipDeclaration(
                    "configuration-parent",
                    unit.Identity,
                    "parentId",
                    "by-parent-id",
                    unit.Identity,
                    PhysicalDocumentFieldPaths.Id,
                    "by-id")
            ]
        };

        var plan = planner.Plan(manifest, RuntimeCapabilities(), CreateCapabilities());

        Assert.False(plan.IsPlannable);
        Assert.Empty(plan.Operations);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "GW-MAT-004");
    }

    [Fact]
    public void PlanRejectsGuardOnlyManifestsBeforeProducingMaterializationOperations()
    {
        var manifest = CreateManifest();
        var unit = Assert.Single(manifest.StorageUnits);
        var logicalIndex = new LogicalIndexDeclaration(
            "by-category",
            [new IndexField("category")],
            IndexValueKind.Keyword,
            false,
            MissingValueBehavior.Excluded);
        var physicalStorage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Explicit(PhysicalTableDefinition.PhysicalEntityTable(
                "configuration_documents",
                [new ProjectedColumnDefinition("category", "category", PortablePhysicalType.String)],
                indexes:
                [
                    new PhysicalIndexDefinition(
                        logicalIndex.Identity,
                        [
                            new PhysicalIndexColumnDefinition("storage_scope", 0),
                            new PhysicalIndexColumnDefinition("category", 1)
                        ])
                ])),
            [logicalIndex],
            [new BoundedQueryDeclaration(
                "list-by-category",
                logicalIndex.Identity,
                new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                QuerySortSupport.None,
                QueryPagingSupport.None,
                BoundedQueryExecutionClass.ScaleBearing)],
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "guarded-delete",
                    "list-by-category",
                    BoundedMutationAction.Delete(),
                    [BoundedMutationRelationshipGuard.RequireNoReferences("missing-relationship")])
            ]);
        manifest = manifest with
        {
            StorageUnits = [unit with { PhysicalStorage = physicalStorage }],
            Relationships = []
        };

        var plan = planner.Plan(manifest, RuntimeCapabilities(), CreateCapabilities());

        Assert.False(plan.IsPlannable);
        Assert.Empty(plan.Operations);
        var diagnostic = Assert.Single(plan.Diagnostics);
        Assert.Equal("GW-MAT-004", diagnostic.Code);
        Assert.Contains("missing-relationship", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanReturnsRuntimeFitDiagnosticsAndNoOperations()
    {
        var manifest = CreateManifest(
            StorageIntent.Operational(
                "Needs an atomic claim provider for worker coordination.",
                WorkloadIntent.OperationalStream,
                WellKnownCapabilities.AtomicClaim));
        var runtimeCapabilities = RuntimeCapabilities();

        var plan = planner.Plan(manifest, runtimeCapabilities, CreateCapabilities());

        Assert.False(plan.IsPlannable);
        Assert.Empty(plan.Operations);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "GW-CAP-004");
    }

    [Fact]
    public void PlanUsesMaterializationCapabilitiesInsteadOfRuntimeMaterializationFields()
    {
        var manifest = CreateManifest();
        var runtimeCapabilities = RuntimeCapabilities();

        var plan = planner.Plan(manifest, runtimeCapabilities, CreateCapabilities());

        Assert.True(plan.IsPlannable);
        Assert.Empty(plan.Diagnostics);
        Assert.IsType<RecordSchemaHistoryOperation>(plan.Operations[^1]);
    }

    [Fact]
    public void RuntimeProviderCapabilityReportDoesNotCarryMaterializationCapabilityFields()
    {
        var propertyNames = typeof(ProviderCapabilityReport)
            .GetProperties()
            .Select(property => property.Name);

        Assert.DoesNotContain("SupportedMaterializationOperations", propertyNames);
        Assert.DoesNotContain("SupportsSchemaHistory", propertyNames);
    }

    [Fact]
    public void MaterializationProjectDependsOnCoreWithoutCoreDependingOnMaterialization()
    {
        var repositoryRoot = TestInfrastructure.RepositoryRootLocator.FindRepositoryRoot();
        var materializationProject = File.ReadAllText(Path.Combine(repositoryRoot, "src/Groundwork/Materialization/Groundwork.Materialization.csproj"));
        var coreProject = File.ReadAllText(Path.Combine(repositoryRoot, "src/Groundwork/Core/Groundwork.Core.csproj"));

        Assert.Contains("..\\Core\\Groundwork.Core.csproj", materializationProject);
        Assert.DoesNotContain("Groundwork.Materialization.csproj", coreProject);
    }

    private MaterializationCapabilityReport CreateCapabilities(
        ProviderIdentity? provider = null,
        IReadOnlySet<MaterializationOperationKind>? supportedOperations = null,
        bool supportsSchemaHistory = true) =>
        new(
            provider ?? this.provider,
            supportedOperations ?? Enum.GetValues<MaterializationOperationKind>().ToHashSet(),
            supportsSchemaHistory);

    private ProviderCapabilityReport RuntimeCapabilities() =>
        new(
            provider,
            new HashSet<CapabilityId>(),
            new HashSet<CapabilityId>(),
            IndexCapabilities.All,
            Enum.GetValues<PortableQueryOperation>().ToHashSet(),
            Enum.GetValues<ConcurrencyKind>().ToHashSet(),
            []);

    private static StorageManifest CreateManifest(StorageIntent? intent = null) =>
        new(
            new StorageManifestIdentity("configuration.documents"),
            new StorageManifestOwner("sample.application"),
            new StorageManifestVersion("1.0.0"),
            [
                new StorageUnit(
                    new StorageUnitIdentity("configurationDocument"),
                    "Configuration document",
                    intent ?? StorageIntent.PortableDocument(),
                    LifecyclePolicy.Mutable,
                    IdentityPolicy.StringId(),
                    TenancyPolicy.Scoped,
                    ConcurrencyPolicy.Optimistic(),
                    SerializationPolicy.Json(),
                    [
                        new IndexDeclaration(
                            "by-key",
                            [new IndexField("key")],
                            IndexValueKind.Keyword,
                            true,
                            true,
                            MissingValueBehavior.Excluded,
                            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                            IndexPhysicalizationPolicy.Optimized),
                        new IndexDeclaration(
                            "by-category",
                            [new IndexField("category")],
                            IndexValueKind.String,
                            false,
                            true,
                            MissingValueBehavior.Excluded,
                            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal, PortableQueryOperation.StartsWith })
                    ],
                    [
                        new PortableQueryDeclaration(
                            "find-by-key",
                            "by-key",
                            new HashSet<PortableQueryOperation> { PortableQueryOperation.Equal },
                            QuerySortSupport.None,
                            QueryPagingSupport.None)
                    ],
                    PhysicalizationPolicy.Portable)
            ],
            new HashSet<string> { "schema-history", "optimistic-concurrency" },
            ["Sample manifest for materialization planning tests."]);
}
