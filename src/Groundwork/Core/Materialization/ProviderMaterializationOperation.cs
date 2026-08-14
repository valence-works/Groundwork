namespace Groundwork.Core.Materialization;

/// <summary>
/// Provider-facing storage-preparation operation kinds. Route-native schema evolution uses
/// <see cref="BackfillCanonicalJson"/> through <see cref="IProviderMaterializationOperation"/>.
/// </summary>
public enum MaterializationOperationKind
{
    CreateStorageUnit,
    CreateIndex,
    BackfillCanonicalJson,
    CreateOptimizedProjection,
    RecordSchemaHistory
}

/// <summary>Execution contract implemented by route-native canonical JSON backfill operations.</summary>
public interface IProviderMaterializationOperation
{
    MaterializationOperationKind Kind { get; }

    string Target { get; }
}
