namespace Dytools.VirtualFileSystem.Catalog;

/// <summary>
/// Which <see cref="IVfsCatalog"/> a catalog-using node should resolve. A provider's Use…Catalog extension stashes
/// one of these in the mount options bag; the node reads it and passes the values to <see cref="CatalogResolver"/>.
/// <see cref="ServiceKey"/> picks a keyed registration (null = the default one); <see cref="Partition"/> isolates the mount within
/// a shared, partition-capable catalog (null = the un-partitioned view).
/// </summary>
public sealed class CatalogSelection
{
    /// <summary>Picks a keyed <see cref="IVfsCatalog"/> registration; null selects the default one.</summary>
    public object? ServiceKey { get; set; }

    /// <summary>Isolates the mount within a shared, partition-capable catalog; null selects the un-partitioned view.</summary>
    public string? Partition  { get; set; }
}
