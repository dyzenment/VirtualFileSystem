using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.Dedupe;

public static class DedupeBuilderExtensions
{
    // Mounts a DedupeNode wrapping the node built by `inner`. The catalog is resolved from
    // DI at build time — register one (e.g. services.AddVfsJsonCatalog(...)); there is no
    // implicit default.
    //
    //   catalogServiceKey    — selects a keyed IVfsCatalog; null uses the default registration.
    //   catalogPartitionKey  — an isolated partition of that catalog, for when one catalog
    //                          backs several mounts (requires an IPartitionedVfsCatalog).
    //
    //   services.AddVfsJsonCatalog(sp => sp.NodeAt("/disk/catalog"));
    //   services.AddVirtualFileSystem()
    //       .MountDeduplicated("/files", sp => sp.NodeAt("/disk/files"));
    public static IVfsBuilder MountDeduplicated(
        this IVfsBuilder builder,
        string mountPoint,
        Func<IServiceProvider, IVfsNode> inner,
        string? catalogPartitionKey = null,
        object? catalogServiceKey   = null,
        DedupeOptions? options       = null,
        MountLifetime lifetime       = MountLifetime.Singleton)
        => builder.Mount(mountPoint,
            sp => new DedupeNode(
                inner(sp),
                ResolveCatalog(sp, mountPoint, catalogServiceKey, catalogPartitionKey),
                options),
            lifetime);

    private static IVfsCatalog ResolveCatalog(
        IServiceProvider sp, string mountPoint, object? serviceKey, string? partitionKey)
    {
        var catalog = serviceKey is null
            ? sp.GetService<IVfsCatalog>()
            : sp.GetKeyedService<IVfsCatalog>(serviceKey);

        if (catalog is null)
            throw new InvalidOperationException(
                $"No IVfsCatalog registered for dedupe mount '{mountPoint}'"
                + (serviceKey is null ? "" : $" (service key '{serviceKey}')")
                + ". Register one, e.g. services.AddVfsJsonCatalog(sp => sp.NodeAt(\"/path\")).");

        if (partitionKey is null) return catalog;

        if (catalog is IPartitionedVfsCatalog partitioned) return partitioned.ForPartition(partitionKey);

        throw new InvalidOperationException(
            $"The catalog for dedupe mount '{mountPoint}' does not support partitioning "
            + $"(partition key '{partitionKey}'); it must implement {nameof(IPartitionedVfsCatalog)}.");
    }
}
