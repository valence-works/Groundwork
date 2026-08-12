using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.Queries;
using Groundwork.Core.Scoping;
using Groundwork.Core.Text;
using Groundwork.Core.Transactions;
using Groundwork.Documents.Scoping;
using Groundwork.Documents.Store;
using Groundwork.Documents.UnitOfWork;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Groundwork.Tests;

public sealed partial class PhysicalQueryPlanCompilerTests
{
    [Fact]
    public void Relationship_guards_require_complete_manifest_route_admission()
    {
        var fixture = CreateFixture(
            PhysicalStorageForm.PhysicalEntityTable,
            BoundedQueryExecutionClass.ScaleBearing);
        var storage = new StorageUnitPhysicalStorage(
            fixture.Storage.ProvisioningMode,
            fixture.Storage.Policy,
            fixture.Storage.LogicalIndexes,
            fixture.Storage.BoundedQueries,
            fixture.Storage.NameOverrides,
            boundedMutations:
            [
                new BoundedMutationDeclaration(
                    "guarded-prune",
                    "list-by-stimulus-type",
                    BoundedMutationAction.Delete(),
                    [BoundedMutationRelationshipGuard.RequireNoReferences("token-authorization")])
            ]);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.Route,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-009");
    }

    [Fact]
    public void Manifest_relationship_requires_a_stable_source_reference_path()
    {
        var source = new StorageUnitIdentity("token");
        var target = new StorageUnitIdentity("authorization");

        Assert.Throws<ArgumentException>(() => new ManifestRelationshipDeclaration(
            "token-authorization",
            source,
            null!,
            "token-by-authorization-id",
            target,
            PhysicalDocumentFieldPaths.Id,
            "authorization-by-id"));
        Assert.Throws<ArgumentException>(() => new ManifestRelationshipDeclaration(
            "token-authorization",
            source,
            " ",
            "token-by-authorization-id",
            target,
            PhysicalDocumentFieldPaths.Id,
            "authorization-by-id"));
    }

    [Fact]
    public void No_reference_guard_binds_the_referencing_route_and_indexed_scalar_path()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var guard = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(Assert.Single(result.Plans).RelationshipGuards));
        Assert.Equal("token-authorization", guard.Relationship.Identity);
        Assert.Equal("token", guard.Relationship.SourceRoute.StorageUnit.Value);
        Assert.Equal("authorizationId", guard.Relationship.SourceCanonicalJsonReference.Path);
        Assert.Equal(PhysicalQueryFieldSource.CanonicalJsonPath, guard.Relationship.SourceCanonicalJsonReference.Source);
        Assert.Equal("token-by-authorization-id", guard.Relationship.SourceReferenceDeclarationIndex.Identity);
        Assert.Equal("authorization", guard.Relationship.TargetRoute.StorageUnit.Value);
        Assert.Equal("authorization-by-id", guard.Relationship.TargetEqualityIndex.Identity);
    }

    [Fact]
    public void Related_target_guard_binds_route_evidence_and_changes_the_request_fingerprint()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: true);
        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var plan = Assert.Single(result.Plans);
        var guard = Assert.IsType<PhysicalRequireRelatedTargetNotEqualMutationGuard>(
            Assert.Single(plan.RelationshipGuards));
        Assert.Equal("authorizationId", guard.Relationship.SourceCanonicalJsonReference.Path);
        Assert.Equal("authorization", guard.Relationship.TargetRoute.StorageUnit.Value);
        Assert.Equal("status", guard.TargetPredicateField.Path);
        Assert.Equal("authorization-by-status", guard.TargetPredicateIndex.Identity);
        Assert.Equal("valid", guard.DisallowedTargetValue);

        var changed = plan with
        {
            RelationshipGuards =
            [
                new PhysicalRequireRelatedTargetNotEqualMutationGuard(
                    guard.Relationship,
                    guard.TargetPredicateField,
                    guard.TargetPredicateIndex,
                    "revoked")
            ]
        };
        var request = new DocumentMutation(
            "token",
            "guarded-prune",
            "operation-1",
            [DocumentQueryClause.Of(DocumentQueryComparison.Equal("status", "invalid"))]);

        Assert.NotEqual(
            BoundedMutationRequestFingerprint.Create(request, plan, "global"),
            BoundedMutationRequestFingerprint.Create(request, changed, "global"));

        var providerChanged = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(
                new ProviderIdentity("provider-after-upgrade", "9.0"),
                PhysicalQuerySourceKind.PrimaryProjectedColumns)).Plans);
        Assert.NotEqual(plan.Fingerprint, providerChanged.Fingerprint);
        Assert.Equal(
            BoundedMutationRequestFingerprint.Create(request, plan, "global"),
            BoundedMutationRequestFingerprint.Create(request, providerChanged, "global"));
    }

    [Fact]
    public void Relationship_guard_rejects_an_unindexed_reference_path()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false, includeReferenceIndex: false);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-RELATIONSHIP-010");
    }

    [Fact]
    public void Relationship_plan_preserves_an_optional_missing_reference_and_projects_present_values_with_the_target_policy()
    {
        var fixture = CreateRelationshipFixture(
            relatedTarget: false,
            includeMutation: false,
            targetIdentityCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
            referenceCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);

        var plan = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(fixture.RouteSet).Plans);

        Assert.Null(plan.ProjectReference(null));
        Assert.Throws<ArgumentException>(() => plan.ProjectReference(""));
        Assert.Throws<ArgumentException>(() => plan.ProjectReference(" "));
        Assert.Null(plan.ProjectReferenceIdentity(null));
        var identity = Assert.IsType<PortableStringIdentityProjection>(plan.ProjectReferenceIdentity("AUTH-1"));
        Assert.Equal(
            plan.TargetRoute.Envelope.Identity.Project("AUTH-1").ComparisonKey,
            plan.ProjectReference("AUTH-1"));
        Assert.Equal(plan.TargetRoute.Envelope.Identity.Project("AUTH-1"), identity);
        Assert.Equal(PhysicalQueryFieldSource.CanonicalJsonPath, plan.SourceCanonicalJsonReference.Source);
        Assert.NotEqual(
            plan.SourceReferenceDeclarationIndex.Name,
            plan.TargetEqualityIndex.Name);
    }

    [Fact]
    public void Relationship_plan_exposes_a_canonical_collision_safe_reference_and_fence_schema()
    {
        var plan = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(relatedTarget: false, includeMutation: false).RouteSet).Plans);

        var schema = plan.MaterializationSchema;

        Assert.Equal(plan.Identity, schema.RelationshipIdentity);
        Assert.Equal(plan.Materialization.ReferenceStorageIdentity, schema.GenerationIdentity);
        Assert.Equal(plan.Materialization.ReferenceStorageIdentity, schema.Reference.StorageIdentity);
        Assert.Equal(plan.Materialization.FenceStorageIdentity, schema.Fence.StorageIdentity);
        Assert.Equal(
            [
                PhysicalRelationshipSidecarField.MaterializationGeneration,
                PhysicalRelationshipSidecarField.SourceScope,
                PhysicalRelationshipSidecarField.SourceLookupKey,
                PhysicalRelationshipSidecarField.SourceComparisonKey,
                PhysicalRelationshipSidecarField.TargetScope,
                PhysicalRelationshipSidecarField.TargetLookupKey,
                PhysicalRelationshipSidecarField.TargetComparisonKey
            ],
            schema.Reference.Fields);
        Assert.Equal(
            [
                PhysicalRelationshipSidecarField.MaterializationGeneration,
                PhysicalRelationshipSidecarField.SourceScope,
                PhysicalRelationshipSidecarField.SourceLookupKey,
                PhysicalRelationshipSidecarField.SourceComparisonKey
            ],
            schema.Reference.UniqueSourceAccessPath.Fields);
        Assert.True(schema.Reference.UniqueSourceAccessPath.IsUnique);
        Assert.Equal(
            [
                PhysicalRelationshipSidecarField.MaterializationGeneration,
                PhysicalRelationshipSidecarField.TargetScope,
                PhysicalRelationshipSidecarField.TargetLookupKey,
                PhysicalRelationshipSidecarField.TargetComparisonKey
            ],
            schema.Reference.TargetSeekAccessPath.Fields);
        Assert.False(schema.Reference.TargetSeekAccessPath.IsUnique);
        Assert.Equal(schema.Reference.TargetSeekAccessPath.Fields, schema.Fence.Fields);
        Assert.Equal(schema.Fence.Fields, schema.Fence.UniqueTargetFenceAccessPath.Fields);
        Assert.True(schema.Fence.UniqueTargetFenceAccessPath.IsUnique);
        Assert.Contains("\"SourceLookupKey\",\"SourceComparisonKey\"", schema.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"TargetLookupKey\",\"TargetComparisonKey\"", schema.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Relationship_sidecar_schema_is_deterministic_and_tracks_semantic_generation_only()
    {
        var baseline = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(relatedTarget: false, includeMutation: false).RouteSet).Plans)
            .MaterializationSchema;
        var same = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(relatedTarget: false, includeMutation: false).RouteSet).Plans)
            .MaterializationSchema;
        var renamed = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                authorizationPhysicalName: "authorizations_v2",
                tokenPhysicalName: "tokens_v2").RouteSet).Plans)
            .MaterializationSchema;
        var semanticDrift = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                targetIdentityCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
                referenceCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase).RouteSet).Plans)
            .MaterializationSchema;

        Assert.Equal(baseline.CanonicalJson, same.CanonicalJson);
        Assert.Equal(baseline.Fingerprint, same.Fingerprint);
        Assert.Equal(baseline, same);
        Assert.Equal(baseline.CanonicalJson, renamed.CanonicalJson);
        Assert.Equal(baseline.Fingerprint, renamed.Fingerprint);
        Assert.Equal(baseline, renamed);
        Assert.NotEqual(baseline.GenerationIdentity, semanticDrift.GenerationIdentity);
        Assert.NotEqual(baseline.Fingerprint, semanticDrift.Fingerprint);
    }

    [Fact]
    public void Relationship_sidecar_schema_canonical_payload_and_fingerprint_bind_every_generated_identity()
    {
        var schema = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(relatedTarget: false, includeMutation: false).RouteSet).Plans)
            .MaterializationSchema;
        var expectedPayload =
            $"{{\"schemaVersion\":1,\"relationship\":\"{schema.RelationshipIdentity}\",\"generation\":\"{schema.GenerationIdentity}\"," +
            $"\"reference\":{{\"storageIdentity\":\"{schema.Reference.StorageIdentity}\",\"generation\":\"{schema.Reference.GenerationIdentity}\"," +
            "\"fields\":[\"MaterializationGeneration\",\"SourceScope\",\"SourceLookupKey\",\"SourceComparisonKey\",\"TargetScope\",\"TargetLookupKey\",\"TargetComparisonKey\"]," +
            $"\"uniqueSourceAccessPath\":{{\"identity\":\"{schema.Reference.UniqueSourceAccessPath.Identity}\",\"unique\":true," +
            "\"fields\":[\"MaterializationGeneration\",\"SourceScope\",\"SourceLookupKey\",\"SourceComparisonKey\"]}," +
            $"\"targetSeekAccessPath\":{{\"identity\":\"{schema.Reference.TargetSeekAccessPath.Identity}\",\"unique\":false," +
            "\"fields\":[\"MaterializationGeneration\",\"TargetScope\",\"TargetLookupKey\",\"TargetComparisonKey\"]}}," +
            $"\"fence\":{{\"storageIdentity\":\"{schema.Fence.StorageIdentity}\",\"generation\":\"{schema.Fence.GenerationIdentity}\"," +
            "\"fields\":[\"MaterializationGeneration\",\"TargetScope\",\"TargetLookupKey\",\"TargetComparisonKey\"]," +
            $"\"uniqueTargetFenceAccessPath\":{{\"identity\":\"{schema.Fence.UniqueTargetFenceAccessPath.Identity}\",\"unique\":true," +
            "\"fields\":[\"MaterializationGeneration\",\"TargetScope\",\"TargetLookupKey\",\"TargetComparisonKey\"]}}}";
        var expectedFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(expectedPayload))).ToLowerInvariant();
        var expectedCanonicalJson =
            $"{expectedPayload[..^1]},\"fingerprint\":\"{expectedFingerprint}\"}}";

        Assert.Equal(expectedFingerprint, schema.Fingerprint);
        Assert.Equal(expectedCanonicalJson, schema.CanonicalJson);

        var alteredReference = new PhysicalRelationshipReferenceMaterializationSchema(
            schema.Reference.StorageIdentity,
            schema.Reference.GenerationIdentity,
            schema.Reference.UniqueSourceAccessPath,
            new PhysicalRelationshipSidecarAccessPath(
                "relationship-reference-by-target:altered",
                isUnique: false,
                schema.Reference.TargetSeekAccessPath.Fields));
        var altered = new PhysicalRelationshipMaterializationSchema(
            schema.RelationshipIdentity,
            schema.GenerationIdentity,
            alteredReference,
            schema.Fence);

        Assert.Equal(schema.GenerationIdentity, altered.GenerationIdentity);
        Assert.NotEqual(schema.Fingerprint, altered.Fingerprint);
    }

    [Fact]
    public void Relationship_sidecar_schema_rejects_non_contract_access_paths_and_mixed_generations()
    {
        var schema = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(relatedTarget: false, includeMutation: false).RouteSet).Plans)
            .MaterializationSchema;
        var reference = schema.Reference;
        var fence = schema.Fence;

        var sourceWithWrongOrder = new PhysicalRelationshipSidecarAccessPath(
            "invalid-source-order",
            isUnique: true,
            [
                PhysicalRelationshipSidecarField.MaterializationGeneration,
                PhysicalRelationshipSidecarField.SourceScope,
                PhysicalRelationshipSidecarField.SourceComparisonKey,
                PhysicalRelationshipSidecarField.SourceLookupKey
            ]);
        var targetWithWrongUniqueness = new PhysicalRelationshipSidecarAccessPath(
            "invalid-target-uniqueness",
            isUnique: true,
            reference.TargetSeekAccessPath.Fields);
        var targetWithDuplicateIdentity = new PhysicalRelationshipSidecarAccessPath(
            reference.UniqueSourceAccessPath.Identity,
            isUnique: false,
            reference.TargetSeekAccessPath.Fields);
        var referenceWithMismatchedStorageGeneration =
            new PhysicalRelationshipReferenceMaterializationSchema(
                "relationship-reference:old-generation",
                schema.GenerationIdentity,
                reference.UniqueSourceAccessPath,
                reference.TargetSeekAccessPath);
        var differentGenerationFence = new PhysicalRelationshipTargetFenceSchema(
            fence.StorageIdentity,
            "relationship-reference:other-generation",
            fence.UniqueTargetFenceAccessPath);

        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipReferenceMaterializationSchema(
            reference.StorageIdentity,
            reference.GenerationIdentity,
            sourceWithWrongOrder,
            reference.TargetSeekAccessPath));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipReferenceMaterializationSchema(
            reference.StorageIdentity,
            reference.GenerationIdentity,
            reference.UniqueSourceAccessPath,
            targetWithWrongUniqueness));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipReferenceMaterializationSchema(
            reference.StorageIdentity,
            reference.GenerationIdentity,
            reference.UniqueSourceAccessPath,
            targetWithDuplicateIdentity));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipMaterializationSchema(
            schema.RelationshipIdentity,
            schema.GenerationIdentity,
            referenceWithMismatchedStorageGeneration,
            fence));
        Assert.Throws<ArgumentException>(() => new PhysicalRelationshipMaterializationSchema(
            schema.RelationshipIdentity,
            schema.GenerationIdentity,
            reference,
            differentGenerationFence));
    }

    [Fact]
    public void Every_manifest_relationship_is_admitted_even_when_no_mutation_guard_references_it()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false, includeMutation: false);

        var relationships = PhysicalRelationshipPlanCompiler.Compile(fixture.RouteSet);
        var mutations = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.True(relationships.IsValid, string.Join("; ", relationships.Diagnostics.Select(item => item.Message)));
        Assert.Single(relationships.Plans);
        Assert.True(mutations.IsValid, string.Join("; ", mutations.Diagnostics.Select(item => item.Message)));
        Assert.Empty(mutations.Plans);
    }

    [Fact]
    public void Provider_admission_rejects_relationship_manifests_without_a_public_preview_override()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false, includeMutation: false);
        var provider = new ProviderIdentity("provider-under-test", "1.0");

        var exception = Assert.Throws<PhysicalRelationshipProviderNotSupportedException>(() =>
            PhysicalRelationshipProviderAdmission.RequireMaterializationSupport(
                fixture.Manifest,
                provider));

        Assert.Contains("GW-RELATIONSHIP-012", exception.Message);
        Assert.Equal(["token-authorization"], exception.RelationshipIdentities);
        Assert.Equal(
            2,
            typeof(PhysicalRelationshipProviderAdmission)
                .GetMethod(nameof(PhysicalRelationshipProviderAdmission.RequireMaterializationSupport))!
                .GetParameters()
                .Length);
    }

    [Fact]
    public void Relationship_materialization_identity_rejects_invalid_unicode_before_hashing()
    {
        var invalid = new string('\uD800', 1);
        var replacement = "\uFFFD";
        var invalidFixtures = new[]
        {
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                manifestIdentity: $"manifest-{invalid}"),
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                tokenIdentity: $"token-{invalid}"),
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                sourceReferencePath: $"authorization-{invalid}"),
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                sourceReferenceIndexIdentity: $"token-by-authorization-{invalid}")
        };

        Assert.All(invalidFixtures, fixture =>
            Assert.Throws<ArgumentException>(() =>
                PhysicalRelationshipPlanCompiler.Compile(fixture.RouteSet)));

        var replacementPlan = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(
                relatedTarget: false,
                includeMutation: false,
                manifestIdentity: $"manifest-{replacement}").RouteSet).Plans);
        Assert.NotEmpty(replacementPlan.Materialization.ReferenceStorageIdentity);
    }

    [Fact]
    public void Provider_admission_rejects_guarded_mutations_without_relationship_declarations()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var guardOnly = fixture.Manifest with { Relationships = [] };
        var provider = new ProviderIdentity("provider-under-test", "1.0");

        var exception = Assert.Throws<PhysicalRelationshipProviderNotSupportedException>(() =>
            PhysicalRelationshipProviderAdmission.RequireMaterializationSupport(
                guardOnly,
                provider));

        Assert.Equal(["token-authorization"], exception.RelationshipIdentities);
    }

    [Fact]
    public void Relationship_admission_rejects_non_string_and_collection_reference_paths()
    {
        var nonString = CreateRelationshipFixture(
            relatedTarget: false,
            sourceReferenceType: PortablePhysicalType.Decimal,
            sourceReferenceValueKind: IndexValueKind.Number);
        var collection = CreateRelationshipFixture(
            relatedTarget: false,
            sourceReferenceCardinality: ProjectionCardinality.CollectionElements);

        var nonStringResult = PhysicalRelationshipPlanCompiler.Compile(nonString.RouteSet);
        var collectionResult = PhysicalRelationshipPlanCompiler.Compile(collection.RouteSet);

        Assert.Contains(nonStringResult.Diagnostics, diagnostic => diagnostic.Code == "GW-RELATIONSHIP-010");
        Assert.Contains(collectionResult.Diagnostics, diagnostic => diagnostic.Code == "GW-RELATIONSHIP-010");
    }

    [Fact]
    public void Relationship_admission_rejects_case_and_scope_policy_mismatches()
    {
        var caseMismatch = CreateRelationshipFixture(
            relatedTarget: false,
            targetIdentityCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase);
        var scopeMismatch = CreateRelationshipFixture(
            relatedTarget: false,
            tokenTenancy: TenancyPolicy.Global);

        var caseResult = PhysicalRelationshipPlanCompiler.Compile(caseMismatch.RouteSet);
        var scopeResult = PhysicalRelationshipPlanCompiler.Compile(scopeMismatch.RouteSet);

        Assert.Contains(caseResult.Diagnostics, diagnostic => diagnostic.Code == "GW-RELATIONSHIP-008");
        Assert.Contains(scopeResult.Diagnostics, diagnostic => diagnostic.Code == "GW-RELATIONSHIP-006");
    }

    [Fact]
    public void Relationship_admission_accepts_matching_global_scope_without_scope_index_prefix()
    {
        var fixture = CreateRelationshipFixture(
            relatedTarget: false,
            authorizationTenancy: TenancyPolicy.Global,
            tokenTenancy: TenancyPolicy.Global);

        var result = PhysicalRelationshipPlanCompiler.Compile(fixture.RouteSet);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Single(result.Plans);
    }

    [Theory]
    [InlineData("token-by-status", "authorization-by-id", "GW-RELATIONSHIP-010")]
    [InlineData("missing-source-index", "authorization-by-id", "GW-RELATIONSHIP-010")]
    [InlineData("token-by-authorization-id", "authorization-by-status", "GW-RELATIONSHIP-011")]
    [InlineData("token-by-authorization-id", "missing-target-index", "GW-RELATIONSHIP-011")]
    public void Relationship_admission_rejects_wrong_or_missing_indexes(
        string sourceIndex,
        string targetIndex,
        string expectedCode)
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var relationship = Assert.Single(fixture.Manifest.Relationships);
        var changed = fixture.Manifest with
        {
            Relationships =
            [
                new ManifestRelationshipDeclaration(
                    relationship.Identity,
                    relationship.SourceStorageUnit,
                    relationship.SourceReferencePath,
                    sourceIndex,
                    relationship.TargetStorageUnit,
                    relationship.TargetIdentityPath,
                    targetIndex,
                    relationship.ReferenceCasePolicy)
            ]
        };

        var result = PhysicalRelationshipPlanCompiler.Compile(CompileRelationshipRouteSet(changed));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Theory]
    [InlineData(true, false, "GW-RELATIONSHIP-010")]
    [InlineData(false, true, "GW-RELATIONSHIP-011")]
    public void Relationship_admission_rejects_non_leading_equality_indexes(
        bool nonLeadingSourceReference,
        bool nonLeadingTargetIdentity,
        string expectedCode)
    {
        var fixture = CreateRelationshipFixture(
            relatedTarget: false,
            nonLeadingSourceReference: nonLeadingSourceReference,
            nonLeadingTargetIdentity: nonLeadingTargetIdentity);

        var result = PhysicalRelationshipPlanCompiler.Compile(fixture.RouteSet);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void Related_target_guard_rejects_a_wrong_predicate_index()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: true);
        var changedMutation = new BoundedMutationDeclaration(
            "guarded-prune",
            "prune-tokens",
            BoundedMutationAction.Delete(),
            [BoundedMutationRelationshipGuard.RequireRelatedTargetNotEqual(
                "token-authorization",
                "status",
                "authorization-by-id",
                "valid")]);
        var storage = new StorageUnitPhysicalStorage(
            fixture.MutationStorage.ProvisioningMode,
            fixture.MutationStorage.Policy,
            fixture.MutationStorage.LogicalIndexes,
            fixture.MutationStorage.BoundedQueries,
            fixture.MutationStorage.NameOverrides,
            [changedMutation]);
        var manifest = fixture.Manifest with
        {
            StorageUnits = fixture.Manifest.StorageUnits
                .Select(unit => unit.Identity.Value == "token"
                    ? unit with { PhysicalStorage = storage }
                    : unit)
                .ToArray()
        };

        var result = PhysicalMutationPlanCompiler.Compile(
            CompileRelationshipRouteSet(manifest),
            fixture.MutationRoute,
            storage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-012");
    }

    [Fact]
    public void Related_target_guard_rejects_a_non_leading_predicate_index()
    {
        var fixture = CreateRelationshipFixture(
            relatedTarget: true,
            nonLeadingTargetPredicate: true);

        var result = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-012");
    }

    [Fact]
    public void Relationship_admission_rejects_same_unit_and_duplicate_relationship_topologies()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var authorization = fixture.Manifest.StorageUnits.Single(unit => unit.Identity.Value == "authorization");
        var sameUnit = new ManifestRelationshipDeclaration(
            "authorization-self",
            authorization.Identity,
            "status",
            "authorization-by-status",
            authorization.Identity,
            PhysicalDocumentFieldPaths.Id,
            "authorization-by-id");
        var duplicate = Assert.Single(fixture.Manifest.Relationships);

        var sameUnitResult = PhysicalRelationshipPlanCompiler.Compile(
            CompileRelationshipRouteSet(fixture.Manifest with { Relationships = [sameUnit] }));
        var duplicateResult = ManifestExecutableRouteSetCompiler.Compile(
            fixture.Manifest with { Relationships = [duplicate, duplicate] },
            PhysicalNamePolicy.Identity,
            ProviderPhysicalNameNormalizer.Identity);

        Assert.Contains(sameUnitResult.Diagnostics, diagnostic => diagnostic.Code == "GW-RELATIONSHIP-005");
        Assert.False(duplicateResult.IsValid);
        Assert.Contains(duplicateResult.Diagnostics, diagnostic => diagnostic.Code == "GW-MANIFEST-006");
    }

    [Fact]
    public void Full_mutation_admission_rejects_rogue_local_route_and_storage_instances()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var rogue = CreateRelationshipFixture(
            relatedTarget: false,
            authorizationPhysicalName: "rogue_authorizations");

        var routeResult = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            rogue.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));
        var storageResult = PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            rogue.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.Contains(routeResult.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-019");
        Assert.Contains(storageResult.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-020");
    }

    [Fact]
    public void Complete_manifest_admission_accepts_only_resolver_compiled_sealed_route_sets()
    {
        Assert.Empty(typeof(ManifestExecutableRouteSet).GetConstructors());
        Assert.DoesNotContain(
            typeof(PhysicalRelationshipPlanCompiler).GetMethods(),
            method => method.Name == nameof(PhysicalRelationshipPlanCompiler.Compile) &&
                      method.GetParameters().Any(parameter =>
                          parameter.ParameterType == typeof(IReadOnlyList<ExecutableStorageRoute>)));
        Assert.DoesNotContain(
            typeof(PhysicalMutationPlanCompiler).GetMethods(),
            method => method.Name == nameof(PhysicalMutationPlanCompiler.Compile) &&
                      method.GetParameters().Any(parameter =>
                          parameter.ParameterType == typeof(IReadOnlyList<ExecutableStorageRoute>)));
    }

    [Fact]
    public void Complete_manifest_admission_snapshots_caller_owned_manifest_collections()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var units = fixture.Manifest.StorageUnits.ToList();
        var relationships = fixture.Manifest.Relationships.ToList();
        var callerOwned = new StorageManifest(
            fixture.Manifest.Identity,
            fixture.Manifest.Owner,
            fixture.Manifest.Version,
            units,
            fixture.Manifest.RequiredCapabilities,
            fixture.Manifest.CompatibilityNotes)
        {
            SharedDocumentStorages = fixture.Manifest.SharedDocumentStorages,
            Relationships = relationships
        };
        var mutatedCallerCollections = false;
        var compilation = ManifestExecutableRouteSetCompiler.Compile(
            callerOwned,
            new DelegatePhysicalNamePolicy(context =>
            {
                if (!mutatedCallerCollections)
                {
                    units.Clear();
                    relationships.Clear();
                    mutatedCallerCollections = true;
                }
                return context.FeatureDefaultLogicalName;
            }),
            ProviderPhysicalNameNormalizer.Identity);
        var routeSet = Assert.IsType<ManifestExecutableRouteSet>(compilation.RouteSet);

        Assert.True(mutatedCallerCollections);
        var result = PhysicalRelationshipPlanCompiler.Compile(routeSet);
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Single(result.Plans);
    }

    [Fact]
    public void Mutation_plan_fingerprint_binds_both_relationship_route_fingerprints()
    {
        var baseline = CompileRelationshipMutationPlan(CreateRelationshipFixture(relatedTarget: false));
        var changedSource = CompileRelationshipMutationPlan(CreateRelationshipFixture(
            relatedTarget: false,
            tokenPhysicalName: "tokens_v2"));
        var changedTarget = CompileRelationshipMutationPlan(CreateRelationshipFixture(
            relatedTarget: false,
            authorizationPhysicalName: "authorizations_v2"));

        Assert.NotEqual(baseline.Fingerprint, changedSource.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, changedTarget.Fingerprint);
    }

    [Fact]
    public void Relationship_materialization_identity_survives_compatible_physical_renames()
    {
        var baseline = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(
                CreateRelationshipFixture(relatedTarget: false)).RelationshipGuards));
        var renamed = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(CreateRelationshipFixture(
                relatedTarget: false,
                authorizationPhysicalName: "authorizations_v2",
                tokenPhysicalName: "tokens_v2")).RelationshipGuards));

        Assert.Equal(baseline.Relationship.Materialization, renamed.Relationship.Materialization);
    }

    [Fact]
    public void Relationship_materialization_identity_rotates_on_semantic_case_policy_drift()
    {
        var baseline = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(
                CreateRelationshipFixture(relatedTarget: false)).RelationshipGuards));
        var changed = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(CreateRelationshipFixture(
                relatedTarget: false,
                targetIdentityCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase,
                referenceCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase))
                .RelationshipGuards));
        var changedSource = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(CreateRelationshipFixture(
                relatedTarget: false,
                sourceIdentityCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase))
                .RelationshipGuards));

        Assert.NotEqual(baseline.Relationship.Materialization, changed.Relationship.Materialization);
        Assert.NotEqual(baseline.Relationship.Materialization, changedSource.Relationship.Materialization);
    }

    [Fact]
    public void Relationship_materialization_identity_rotates_on_semantic_path_and_index_drift()
    {
        var baseline = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(
                CreateRelationshipFixture(relatedTarget: false)).RelationshipGuards));
        var changedSourcePath = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(CreateRelationshipFixture(
                relatedTarget: false,
                sourceReferencePath: "authorizationIdV2")).RelationshipGuards));
        var changedSourceIndex = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(CreateRelationshipFixture(
                relatedTarget: false,
                sourceReferenceIndexIdentity: "token-by-authorization-id-v2")).RelationshipGuards));
        var changedTargetIndex = Assert.IsType<PhysicalRequireNoReferencesMutationGuard>(
            Assert.Single(CompileRelationshipMutationPlan(CreateRelationshipFixture(
                relatedTarget: false,
                targetEqualityIndexIdentity: "authorization-by-id-v2")).RelationshipGuards));

        Assert.NotEqual(baseline.Relationship.Materialization, changedSourcePath.Relationship.Materialization);
        Assert.NotEqual(baseline.Relationship.Materialization, changedSourceIndex.Relationship.Materialization);
        Assert.NotEqual(baseline.Relationship.Materialization, changedTargetIndex.Relationship.Materialization);
    }

    [Fact]
    public void Relationship_materialization_identity_binds_scope_and_both_identity_algorithms()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var baseline = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(fixture.RouteSet).Plans);
        var global = Assert.Single(PhysicalRelationshipPlanCompiler.Compile(
            CreateRelationshipFixture(
                relatedTarget: false,
                authorizationTenancy: TenancyPolicy.Global,
                tokenTenancy: TenancyPolicy.Global).RouteSet).Plans);
        var expectedRoot = ExpectedRelationshipMaterializationRoot(fixture.Manifest, baseline);

        Assert.NotEqual(baseline.Materialization, global.Materialization);
        Assert.Equal(
            $"relationship-reference:{expectedRoot}",
            baseline.Materialization.ReferenceStorageIdentity);
        Assert.Equal(
            $"relationship-reference-by-source:{expectedRoot}",
            baseline.Materialization.ReferenceBySourceIndexIdentity);
        Assert.Equal(
            $"relationship-reference-by-target:{expectedRoot}",
            baseline.Materialization.ReferenceByTargetIndexIdentity);
        Assert.Equal(
            $"relationship-fence:{expectedRoot}",
            baseline.Materialization.FenceStorageIdentity);
        Assert.Equal(
            $"relationship-fence-by-target:{expectedRoot}",
            baseline.Materialization.FenceByTargetIndexIdentity);
    }

    [Fact]
    public void Relationship_guard_canonicalization_is_not_ambiguous_when_values_contain_legacy_delimiters()
    {
        var plan = CompileRelationshipMutationPlan(CreateRelationshipFixture(relatedTarget: true));
        var original = Assert.IsType<PhysicalRequireRelatedTargetNotEqualMutationGuard>(
            Assert.Single(plan.RelationshipGuards));
        var firstField = original.TargetPredicateField with { Path = "a\u001fb", Identifier = "c" };
        var secondField = original.TargetPredicateField with { Path = "a", Identifier = "b\u001fc" };
        var firstGuard = new PhysicalRequireRelatedTargetNotEqualMutationGuard(
            original.Relationship,
            firstField,
            original.TargetPredicateIndex,
            original.DisallowedTargetValue);
        var secondGuard = new PhysicalRequireRelatedTargetNotEqualMutationGuard(
            original.Relationship,
            secondField,
            original.TargetPredicateIndex,
            original.DisallowedTargetValue);

        Assert.NotEqual(firstGuard.CanonicalIdentity, secondGuard.CanonicalIdentity);
        Assert.NotEqual(
            (plan with { RelationshipGuards = [firstGuard] }).Fingerprint,
            (plan with { RelationshipGuards = [secondGuard] }).Fingerprint);
    }

    [Fact]
    public void Relationship_guard_fails_closed_without_provider_execution_certification()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var plan = Assert.Single(PhysicalMutationPlanCompiler.Compile(
            fixture.RouteSet,
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns)).Plans);

        var exception = Assert.Throws<PhysicalRelationshipProviderNotSupportedException>(
            () => new PhysicalMutationHandlerCertification(plan));
        Assert.Equal(["token-authorization"], exception.RelationshipIdentities);
    }

    [Fact]
    public void Relationship_guard_rejects_a_missing_manifest_owned_relationship()
    {
        var fixture = CreateRelationshipFixture(relatedTarget: false);
        var withoutRelationship = fixture.Manifest with { Relationships = [] };

        Assert.False(fixture.Manifest.HasSameDefinitionAs(withoutRelationship));

        var result = PhysicalMutationPlanCompiler.Compile(
            CompileRelationshipRouteSet(withoutRelationship),
            fixture.MutationRoute,
            fixture.MutationStorage,
            Capabilities(PhysicalQuerySourceKind.PrimaryProjectedColumns));

        Assert.False(result.IsValid);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "GW-MUTATION-016");
    }
}
