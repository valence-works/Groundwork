using Groundwork.Core.PhysicalStorage;

namespace Groundwork.Core.Manifests;

public sealed partial record StorageManifest
{
    /// <summary>
    /// Compares the complete manifest definition by stable domain identity rather than by the
    /// reference identity of its immutable collection properties.
    /// </summary>
    public bool HasSameDefinitionAs(StorageManifest? other) =>
        other is not null &&
        Identity == other.Identity &&
        Owner == other.Owner &&
        Version == other.Version &&
        RequiredCapabilities.SetEquals(other.RequiredCapabilities) &&
        CompatibilityNotes.SequenceEqual(other.CompatibilityNotes, StringComparer.Ordinal) &&
        SequenceBy(
            SharedDocumentStorages,
            other.SharedDocumentStorages,
            item => item.Binding.Value,
            EqualityComparer<SharedDocumentStorageDefinition>.Default) &&
        SequenceBy(
            Relationships,
            other.Relationships,
            item => item.Identity,
            EqualityComparer<ManifestRelationshipDeclaration>.Default) &&
        UnitsEqual(StorageUnits, other.StorageUnits);

    private static bool UnitsEqual(
        IReadOnlyList<StorageUnit> first,
        IReadOnlyList<StorageUnit> second) =>
        first.Count == second.Count && first.All(unit =>
        {
            var candidate = second.SingleOrDefault(item => item.Identity == unit.Identity);
            return candidate is not null && UnitEquals(unit, candidate);
        });

    private static bool UnitEquals(StorageUnit first, StorageUnit second) =>
        first.Identity == second.Identity &&
        first.DisplayName == second.DisplayName &&
        first.Intent == second.Intent &&
        first.Lifecycle == second.Lifecycle &&
        first.IdentityPolicy == second.IdentityPolicy &&
        first.Tenancy == second.Tenancy &&
        first.Concurrency == second.Concurrency &&
        first.Serialization == second.Serialization &&
        first.PhysicalStorage == second.PhysicalStorage;

    private static bool SequenceBy<T>(
        IReadOnlyList<T> first,
        IReadOnlyList<T> second,
        Func<T, string> identity,
        IEqualityComparer<T> comparer) =>
        first.Count == second.Count && first.All(item =>
        {
            var matches = second.Where(candidate => identity(candidate) == identity(item)).ToArray();
            return matches.Length == 1 && comparer.Equals(item, matches[0]);
        });
}
