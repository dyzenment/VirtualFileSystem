using Dytools.VirtualFileSystem.Internal;

namespace Dytools.VirtualFileSystem;

public static class NodeAtExtensions
{
    // Returns a node that forwards to an existing VFS path - use it as the inner node of
    // a decorator mount so a backend configured once can be reused by many mounts:
    //
    //   .Mount("/azure", sp => new AzureBlobNode(...), MountLifetime.Singleton)
    //   .Mount("/docs",  sp => new DedupeNode(sp.NodeAt("/azure/docs-blobs"),
    //                                         sp.GetRequiredService<IVfsCatalog>()))
    public static IVfsNode NodeAt(this IServiceProvider provider, string vfsPath)
        => new VfsRerouteNode(provider, vfsPath);
}
