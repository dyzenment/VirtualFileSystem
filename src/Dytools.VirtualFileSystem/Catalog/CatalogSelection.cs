namespace Dytools.VirtualFileSystem.Catalog;

// Which IVfsCatalog a catalog-using node should resolve. A provider's Use…Catalog extension stashes
// one of these in the mount options bag; the node reads it and passes the values to CatalogResolver.
// ServiceKey picks a keyed registration (null = the default one); Partition isolates the mount within
// a shared, partition-capable catalog (null = the un-partitioned view).
public sealed class CatalogSelection
{
    public object? ServiceKey { get; set; }
    public string? Partition  { get; set; }
}
