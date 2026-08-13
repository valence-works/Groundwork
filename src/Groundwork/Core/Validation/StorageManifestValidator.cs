using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;

namespace Groundwork.Core.Validation;

public sealed class StorageManifestValidator
{
    public ManifestValidationResult Validate(StorageManifest manifest)
    {
        var diagnostics = new List<GroundworkDiagnostic>();

        ValidateRequired(manifest.Identity.Value, "GW-MANIFEST-001", "Manifest identity is required.", "manifest.identity", diagnostics);
        ValidateRequired(manifest.Owner.Value, "GW-MANIFEST-002", "Manifest owner is required.", "manifest.owner", diagnostics);
        ValidateRequired(manifest.Version.Value, "GW-MANIFEST-003", "Manifest version is required.", "manifest.version", diagnostics);

        if (manifest.StorageUnits.Count == 0)
            diagnostics.Add(GroundworkDiagnostic.Error("GW-MANIFEST-004", "Manifest must declare at least one storage unit.", "manifest.storageUnits"));

        AddDuplicateDiagnostics(
            manifest.StorageUnits.Select(unit => unit.Identity.Value),
            "GW-MANIFEST-005",
            "Storage unit identities must be unique within a manifest.",
            "manifest.storageUnits",
            diagnostics);

        AddDuplicateDiagnostics(
            manifest.Relationships.Select(relationship => relationship.Identity),
            "GW-MANIFEST-006",
            "Relationship identities must be unique within a manifest.",
            "manifest.relationships",
            diagnostics);

        for (var unitIndex = 0; unitIndex < manifest.StorageUnits.Count; unitIndex++)
            ValidateStorageUnit(manifest.StorageUnits[unitIndex], unitIndex, diagnostics);

        return diagnostics.Count == 0 ? ManifestValidationResult.Success : new ManifestValidationResult(diagnostics);
    }

    private static void ValidateStorageUnit(StorageUnit unit, int unitIndex, List<GroundworkDiagnostic> diagnostics)
    {
        var target = $"manifest.storageUnits[{unitIndex}]";
        ValidateRequired(unit.Identity.Value, "GW-UNIT-001", "Storage unit identity is required.", $"{target}.identity", diagnostics);

        if (ProviderNeutralityRules.LooksProviderSpecific(unit.Identity.Value))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-UNIT-002",
                "Storage unit identity must describe provider-neutral intent, not provider-specific physical shape.",
                $"{target}.identity"));
        }

        if (unit.Intent is null)
        {
            diagnostics.Add(GroundworkDiagnostic.Error("GW-UNIT-003", "Storage unit intent is required.", $"{target}.intent"));
        }
        else
        {
            ValidateStorageIntent(unit.Intent, target, diagnostics);
        }

        if (unit.Lifecycle is null)
            diagnostics.Add(GroundworkDiagnostic.Error("GW-UNIT-006", "Storage unit lifecycle policy is required.", $"{target}.lifecycle"));

        if (IdentityPolicyAdmission.Validate(
                unit.IdentityPolicy,
                $"{target}.identityPolicy") is { } identityPolicyDiagnostic)
        {
            diagnostics.Add(identityPolicyDiagnostic);
        }

        if (unit.Tenancy is null)
            diagnostics.Add(GroundworkDiagnostic.Error("GW-UNIT-011", "Storage unit tenancy policy is required.", $"{target}.tenancy"));
        else if (unit.Tenancy.Kind is not TenancyKind.Global and not TenancyKind.Scoped)
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-UNIT-012",
                $"Tenancy policy '{unit.Tenancy.Kind}' has no executable storage-scope handler.",
                $"{target}.tenancy"));

        if (unit.Concurrency is null)
            diagnostics.Add(GroundworkDiagnostic.Error("GW-UNIT-008", "Storage unit concurrency policy is required.", $"{target}.concurrency"));

        if (unit.Serialization is null)
            diagnostics.Add(GroundworkDiagnostic.Error("GW-UNIT-009", "Storage unit serialization policy is required.", $"{target}.serialization"));
    }

    private static void ValidateStorageIntent(StorageIntent intent, string unitTarget, List<GroundworkDiagnostic> diagnostics)
    {
        if (intent.Requirements.Count != 0 && string.IsNullOrWhiteSpace(intent.Rationale))
        {
            diagnostics.Add(GroundworkDiagnostic.Error(
                "GW-UNIT-005",
                "Storage intents that declare requirements must provide a rationale.",
                $"{unitTarget}.intent.rationale"));
        }
    }

    private static void ValidateRequired(
        string? value,
        string code,
        string message,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
            diagnostics.Add(GroundworkDiagnostic.Error(code, message, target));
    }

    private static void AddDuplicateDiagnostics(
        IEnumerable<string> values,
        string code,
        string message,
        string target,
        List<GroundworkDiagnostic> diagnostics)
    {
        if (values.Where(value => !string.IsNullOrWhiteSpace(value)).GroupBy(value => value, StringComparer.Ordinal).Any(group => group.Count() > 1))
            diagnostics.Add(GroundworkDiagnostic.Error(code, message, target));
    }
}
