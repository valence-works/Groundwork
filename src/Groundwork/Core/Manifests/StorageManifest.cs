using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Core.Manifests;

public sealed partial record StorageManifest(
    StorageManifestIdentity Identity,
    StorageManifestOwner Owner,
    StorageManifestVersion Version,
    IReadOnlyList<StorageUnit> StorageUnits,
    IReadOnlySet<string> RequiredCapabilities,
    IReadOnlyList<string> CompatibilityNotes)
{
    /// <summary>
    /// Gets manifest/composition-owned shared document stores. Storage units reference these by
    /// <see cref="SharedStorageBinding"/> and cannot redefine their primary envelope or name.
    /// </summary>
    public IReadOnlyList<SharedDocumentStorageDefinition> SharedDocumentStorages { get; init; } = [];

    /// <summary>
    /// Gets the manifest-owned relationships that bounded mutations may guard. A relationship is
    /// deliberately declared once, independently of its guards, so ordinary writes and guarded
    /// mutations use the same source, target, equality-index, scope, and identity contract.
    /// </summary>
    public IReadOnlyList<ManifestRelationshipDeclaration> Relationships { get; init; } = [];
}
