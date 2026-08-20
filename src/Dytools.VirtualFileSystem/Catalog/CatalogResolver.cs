using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem.Catalog;

/// <summary>
/// Resolves an <see cref="IVfsCatalog"/> from DI by service key + partition. Takes plain values, not mount options,
/// so nodes stay in control: a node reads its own <see cref="CatalogSelection"/> and passes the values here. Throws
/// if no catalog is registered, or a partition is requested from a non-partitionable catalog.
/// </summary>
public static class CatalogResolver
{
    /// <summary>
    /// Resolves the <see cref="IVfsCatalog"/> registered under <paramref name="serviceKey"/> (null = default), optionally
    /// narrowed to <paramref name="partition"/> (null = un-partitioned view).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No catalog is registered, or a partition is requested from a catalog that does not implement <see cref="IPartitionedVfsCatalog"/>.
    /// </exception>
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
