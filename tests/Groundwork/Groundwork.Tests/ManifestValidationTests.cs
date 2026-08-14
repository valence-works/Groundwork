using Groundwork.Core.Capabilities;
using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.Queries;
using Groundwork.Core.Validation;
using Xunit;
using Groundwork.TestInfrastructure;

namespace Groundwork.Tests;

public sealed class ManifestValidationTests
{
    private readonly StorageManifestValidator _validator = new();

    [Fact]
    public void ValidSampleManifestSucceeds()
    {
        var result = _validator.Validate(SampleManifests.MetadataManifest());

        Assert.True(result.IsValid);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void EmptyManifestFails()
    {
        var result = _validator.Validate(SampleManifests.MetadataManifest() with { StorageUnits = [] });

        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-MANIFEST-004");
    }

    [Fact]
    public void MissingStorageIntentFails()
    {
        var manifest = WithSingleUnit(unit => unit with { Intent = null! });

        var result = _validator.Validate(manifest);

        Assert.Contains(result.Errors, diagnostic =>
            diagnostic.Code == "GW-UNIT-003" &&
            diagnostic.Target == "manifest.storageUnits[0].intent");
    }

    [Fact]
    public void MissingTenancyPolicyFails()
    {
        var manifest = WithSingleUnit(unit => unit with { Tenancy = null! });

        var result = _validator.Validate(manifest);

        Assert.Contains(result.Errors, diagnostic =>
            diagnostic.Code == "GW-UNIT-011" &&
            diagnostic.Target == "manifest.storageUnits[0].tenancy");
    }

    [Fact]
    public void CustomPartitionFailsUntilItHasAnExecutableScopeHandler()
    {
        var manifest = WithSingleUnit(unit => unit with
        {
            Tenancy = new TenancyPolicy(TenancyKind.CustomPartition)
        });

        var result = _validator.Validate(manifest);

        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-UNIT-012");
    }

    [Fact]
    public void Non_string_identity_cannot_select_a_non_ordinal_string_case_policy()
    {
        var manifest = WithSingleUnit(unit => unit with
        {
            IdentityPolicy = new IdentityPolicy(
                StorageIdentityKind.Guid,
                "id",
                StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase)
        });

        var result = _validator.Validate(manifest);

        Assert.Contains(result.Errors, diagnostic =>
            diagnostic.Code == "GW-UNIT-013" &&
            diagnostic.Target == "manifest.storageUnits[0].identityPolicy.stringCasePolicy");
    }

    [Theory]
    [InlineData(StorageIdentityKind.Guid)]
    [InlineData(StorageIdentityKind.Composite)]
    public void Non_string_identity_preserves_the_default_ordinal_declaration(StorageIdentityKind kind)
    {
        var manifest = WithSingleUnit(unit => unit with
        {
            IdentityPolicy = new IdentityPolicy(kind, "id")
        });

        var result = _validator.Validate(manifest);

        Assert.DoesNotContain(result.Errors, diagnostic => diagnostic.Code == "GW-UNIT-013");
    }

    [Fact]
    public void MissingSchemaVersionFails()
    {
        var result = _validator.Validate(SampleManifests.MetadataManifest() with { Version = new StorageManifestVersion("") });

        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-MANIFEST-003");
    }

    [Fact]
    public void ProviderSpecificRequiredPhysicalShapeFails()
    {
        var manifests = new[]
        {
            WithSingleUnit(unit => unit with { Identity = new StorageUnitIdentity("table:configuration_documents") }),
            WithSingleUnit(unit => unit with { Identity = new StorageUnitIdentity("postgresql:configuration_documents") })
        };

        foreach (var manifest in manifests)
        {
            var result = _validator.Validate(manifest);
            Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-UNIT-002");
        }
    }

    [Fact]
    public void IntentWithRequirementsRequiresRationale()
    {
        var manifest = WithSingleUnit(unit => unit with
        {
            Intent = new StorageIntent(
                new HashSet<CapabilityId> { TestCapabilities.Primary },
                rationale: null,
                descriptor: WorkloadIntent.OperationalStream)
        });

        var result = _validator.Validate(manifest);

        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "GW-UNIT-005");
    }

    [Fact]
    public void PortableDocumentIntentIsValidWithoutRationale()
    {
        var manifest = WithSingleUnit(unit => unit with { Intent = StorageIntent.PortableDocument() });

        var result = _validator.Validate(manifest);

        Assert.DoesNotContain(result.Errors, diagnostic => diagnostic.Code == "GW-UNIT-005");
    }

    [Fact]
    public void StorageIntentFactoriesNormalizeNullRequirements()
    {
        var intent = StorageIntent.Operational(
            "Requires external coordination.",
            WorkloadIntent.OperationalStream,
            (CapabilityId[]?)null!);

        Assert.Empty(intent.Requirements);
    }

    [Fact]
    public void StorageIntentUsesRequirementSetValueEquality()
    {
        var first = StorageIntent.Operational(
            "Requires task claiming.",
            WorkloadIntent.OperationalStream,
            TestCapabilities.Primary,
            TestCapabilities.Secondary);
        var second = new StorageIntent(
            new HashSet<CapabilityId>
            {
                TestCapabilities.Secondary,
                TestCapabilities.Primary
            },
            "Requires task claiming.",
            WorkloadIntent.OperationalStream);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(StorageIntent.PortableDocument(), StorageIntent.PortableDocument());
    }

    private static StorageManifest WithSingleUnit(Func<StorageUnit, StorageUnit> configure)
    {
        var manifest = SampleManifests.MetadataManifest();
        return manifest with { StorageUnits = [configure(manifest.StorageUnits.Single())] };
    }
}
