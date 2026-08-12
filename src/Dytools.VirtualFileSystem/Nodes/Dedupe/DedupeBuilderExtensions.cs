using Dytools.VirtualFileSystem.Catalog;

namespace Dytools.VirtualFileSystem.Nodes.Dedupe;

public static class DedupeBuilderExtensions
{
    // Mounts a DedupeNode wrapping `inner`. Defaults to Singleton so the default
    // in-memory catalog persists across operations. For a durable/scoped catalog
    // (e.g. an EF Core-backed IVfsCatalog registered scoped), pass a catalog factory
    // and MountLifetime.Scoped so the node shares the request's DbContext.
    //
    //   .MountDeduplicated("/archive", sp => new LocalFsNode("/data/blobs"))
    //
    //   services.AddScoped<IVfsCatalog, EfVfsCatalog>();
    //   .MountDeduplicated("/archive",
    //        sp => sp.NodeAt("/azure/blobs"),
    //        sp => sp.GetRequiredService<IVfsCatalog>(),
    //        lifetime: MountLifetime.Scoped)
    public static IVfsBuilder MountDeduplicated(
        this IVfsBuilder builder,
        string mountPoint,
        Func<IServiceProvider, IVfsNode> inner,
        Func<IServiceProvider, IVfsCatalog>? catalog = null,
        DedupeOptions? options = null,
        MountLifetime lifetime = MountLifetime.Singleton)
        => builder.Mount(mountPoint,
            sp => new DedupeNode(inner(sp), catalog?.Invoke(sp) ?? new InMemoryVfsCatalog(), options),
            lifetime);
}
