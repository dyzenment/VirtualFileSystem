using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem.Catalog;

// Resolves an IVfsCatalog from DI by service key + partition. Takes plain values, not mount options,
// so nodes stay in control: a node reads its own CatalogSelection and passes the values here. Throws
// if no catalog is registered, or a partition is requested from a non-partitionable catalog.
public static class CatalogResolver
{
    public static IVfsCatalog Resolve(IServiceProvider services, object? serviceKey, string? partition)
    {
        var catalog = serviceKey is null
            ? services.GetService<IVfsCatalog>()
            : services.GetKeyedService<IVfsCatalog>(serviceKey);
        if (catalog is null)
            throw new InvalidOperationException(
                "No IVfsCatalog is registered. Register one, e.g. services.AddVfsJsonCatalog(sp => sp.NodeAt(\"/dev/catalog\")).");

        if (partition is null) return catalog;
        if (catalog is IPartitionedVfsCatalog partitioned) return partitioned.ForPartition(partition);
        throw new InvalidOperationException(
            $"The catalog does not support partitioning (key '{partition}'); it must implement {nameof(IPartitionedVfsCatalog)}.");
    }
}
