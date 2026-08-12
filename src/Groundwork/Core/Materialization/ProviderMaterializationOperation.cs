namespace Groundwork.Core.Materialization;

/// <summary>
/// The one kind vocabulary shared by the compatibility <c>Groundwork.Materialization</c> plan and the
/// route-native <c>Groundwork.Core.SchemaEvolution</c> operations, so a provider can schedule both
/// through <see cref="IProviderMaterializationOperation"/> without a second enumeration.
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
