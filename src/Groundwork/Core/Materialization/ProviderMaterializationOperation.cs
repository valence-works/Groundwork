namespace Groundwork.Core.Materialization;

/// <summary>
/// The one kind vocabulary used by route-native <c>Groundwork.Core.SchemaEvolution</c> operations,
/// so providers share one execution contract without defining a second enumeration.
/// </summary>
public enum MaterializationOperationKind
{
    CreateStorageUnit,
    CreateIndex,
    BackfillCanonicalJson,
    CreateOptimizedProjection,
    RecordSchemaHistory
}

/// <summary>Common execution contract for legacy and schema-evolution materialization steps.</summary>
public interface IProviderMaterializationOperation
{
    MaterializationOperationKind Kind { get; }

    string Target { get; }
}
