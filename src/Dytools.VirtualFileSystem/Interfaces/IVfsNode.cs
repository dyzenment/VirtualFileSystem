namespace Dytools.VirtualFileSystem;

/// <summary>
/// A mountable backend that services stream, metadata, and listing operations for the paths beneath it.
/// Nodes only ever see byte streams and receive already-resolved <see cref="VfsNodeRequest"/> requests.
/// </summary>
public interface IVfsNode
{
    /// <summary>Opens a readable stream for the request, or null when the entry does not exist.</summary>
    Task<Stream?>    OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default);

    /// <summary>Opens a writable stream for the request using the given <see cref="VfsWriteOptions.Mode"/>.</summary>
    Task<Stream>     OpenWriteAsync(VfsNodeRequest request, VfsWriteOptions? options = null, CancellationToken ct = default);

    /// <summary>Deletes the entry addressed by the request.</summary>
    Task             DeleteAsync(VfsNodeRequest request, CancellationToken ct = default);

    /// <summary>Copies the entry at <paramref name="src"/> to <paramref name="dst"/>.</summary>
    Task             CopyAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default);

    /// <summary>Moves the entry at <paramref name="src"/> to <paramref name="dst"/>.</summary>
    Task             MoveAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default);

    /// <summary>
    /// Same-parent, same-node rename. <paramref name="newName"/> is a bare filename with no path separators.
    /// Nodes that support a native in-place rename (e.g. <c>File.Move</c>, S3 CopyObject+Delete in one
    /// atomic op) should override this. The default in <c>VfsNodeBase</c> falls back to <see cref="MoveAsync"/>.
    /// </summary>
    Task             RenameAsync(VfsNodeRequest src, string newName, CancellationToken ct = default);

    /// <summary>
    /// Enumerates the children of the request. <paramref name="options"/> is never null when called through
    /// the pipeline (<c>VfsListOptions.Default</c> at minimum).
    /// </summary>
    IAsyncEnumerable<VfsNodeInfo> ListAsync(VfsNodeRequest request, VfsListOptions options, CancellationToken ct = default);

    /// <summary>Returns whether the entry addressed by the request exists.</summary>
    Task<bool>       ExistsAsync(VfsNodeRequest request, CancellationToken ct = default);

    /// <summary>Returns metadata for the entry addressed by the request, or null when it does not exist.</summary>
    Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest request, CancellationToken ct = default);

    /// <summary>
    /// Escape hatch for behaviour tied to <em>one entry</em>. The pipeline does not use it for ordinary
    /// operations.
    /// Return an object bound to <paramref name="relativePath"/> so the capability's own methods need
    /// no path. Decorators override to decide what to forward or block.
    /// </summary>
    /// <param name="relativePath">The entry, relative to this node's mount.</param>
    T? GetEntryCapability<T>(VfsPath relativePath) where T : class, IEntryCapability => null;

    /// <summary>
    /// Escape hatch for behaviour belonging to the node. Consumer-facing, and the pipeline itself does
    /// not use it for ordinary operations - though middleware may ask, as <c>SymlinkMiddleware</c> does
    /// to find out whether a node deals in symlinks at all.
    /// <paramref name="mountPoint"/> is supplied so a capability that addresses entries can accept
    /// absolute VFS paths and map them itself. Capability interfaces are defined by node providers,
    /// not in the core library. Decorators override to decide what to forward or block.
    /// </summary>
    /// <param name="mountPoint">The mount prefix this node is serving.</param>
    T? GetNodeCapability<T>(VfsPath mountPoint) where T : class, INodeCapability => null;
}

/// <summary>Controls how an existing entry is treated when opening a write stream.</summary>
public enum VfsWriteMode
{
    /// <summary>Truncate any existing content and write from the beginning. Creates the entry if absent.</summary>
    Create,

    /// <summary>Seek to the end of any existing content and append. Creates the entry if absent.</summary>
    Append,

    /// <summary>Fail with <see cref="IOException"/> if the entry already exists.</summary>
    CreateNew,
}
