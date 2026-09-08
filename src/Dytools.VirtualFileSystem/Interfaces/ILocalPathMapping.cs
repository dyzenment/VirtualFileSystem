namespace Dytools.VirtualFileSystem;

/// <summary>
/// A node's translation between its own storage in the host filesystem and mount-relative VFS paths.
/// Implemented by nodes whose entries genuinely are files on this machine; the VFS uses it to answer
/// <c>TryGetVfsPath</c> and <c>TryGetLocalPath</c>.
/// <para>
/// The node is the only thing that can answer this - the registry holds an <see cref="IVfsNode"/> and
/// has no idea what storage sits behind it. Nothing in the core knows what a drive letter is; a node
/// simply reports whether a given host path is one of its own.
/// </para>
/// </summary>
/// <remarks>
/// A decorator should implement this only when its own entries map to host paths. A node that stores
/// entries in some rearranged form - a content-addressed blob store, an archive - must not forward it
/// from the node underneath, because the file on disk is not the entry the caller asked about.
/// </remarks>
public interface ILocalPathMapping : INodeCapability
{
    /// <summary>
    /// The host path an entry would occupy, or null when the relative path falls outside this node's
    /// root.
    /// <para>
    /// For an entry that exists, ask <c>GetInfoAsync</c> - <see cref="VfsEntryInfo.LocalPath"/>
    /// carries it and costs nothing extra. This is here for the case that cannot answer: where a
    /// file <em>would</em> be written, before anything has been.
    /// </para>
    /// </summary>
    /// <param name="relativePath">The entry, relative to this node's mount.</param>
    string? ToLocalPath(VfsPath relativePath);

    /// <summary>
    /// The mount-relative path for a host path, or false when it is not under this node's root.
    /// An exact match on the root itself succeeds with an empty <paramref name="relativePath"/>.
    /// </summary>
    /// <param name="localPath">An absolute host path. Normalised by the implementation.</param>
    /// <param name="relativePath">The path relative to this node's mount, when it maps.</param>
    bool TryGetRelativePath(string localPath, out VfsPath relativePath);
}
