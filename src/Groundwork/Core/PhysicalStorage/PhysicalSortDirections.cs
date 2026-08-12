namespace Groundwork.Core.PhysicalStorage;

/// <summary>
/// Sort-direction algebra shared by physical-storage resolution and query planning.
/// </summary>
internal static class PhysicalSortDirections
{
    internal static PhysicalSortDirection Opposite(PhysicalSortDirection direction) => direction switch
    {
        PhysicalSortDirection.Ascending => PhysicalSortDirection.Descending,
        PhysicalSortDirection.Descending => PhysicalSortDirection.Ascending,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
    };
}
