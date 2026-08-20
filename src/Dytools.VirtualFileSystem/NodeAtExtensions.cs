using Dytools.VirtualFileSystem.Internal;

namespace Dytools.VirtualFileSystem;

/// <summary>
/// Extension for building a node that forwards to an existing VFS path.
/// </summary>
public static class NodeAtExtensions
{
    /// <summary>
    /// Returns a node that forwards to an existing VFS path - use it as the inner node of
    /// a decorator mount so a backend configured once can be reused by many mounts:
    /// <code>
    ///   .Mount("/azure", sp =&gt; new AzureBlobNode(...), MountLifetime.Singleton)
    ///   .Mount("/docs",  sp =&gt; new DedupeNode(sp.NodeAt("/azure/docs-blobs"),
    ///                                         sp.GetRequiredService&lt;IVfsCatalog&gt;()))
    /// </code>
    /// </summary>
    /// <param name="provider">The service provider used to resolve the VFS the reroute targets.</param>
    /// <param name="vfsPath">The absolute VFS path this node forwards to.</param>
    public static IVfsNode NodeAt(this IServiceProvider provider, string vfsPath)
        => new VfsRerouteNode(provider, vfsPath);
}
