namespace Dytools.VirtualFileSystem;

/// <summary>
/// A mountable backend that services stream, metadata, and listing operations for the paths beneath it.
/// Nodes only ever see byte streams and receive already-resolved <see cref="VfsNodeRequest"/> requests.
/// </summary>
public interface IVfsNode
{
    /// <summary>Opens a readable stream for the request, or null when the entry does not exist.</summary>
    Task<Stream?>    OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default);

    /// <summary>Opens a writable stream for the request using the given <paramref name="mode"/>.</summary>
    Task<Stream>     OpenWriteAsync(VfsNodeRequest request, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default);

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
    /// Consumer escape hatch - the core NEVER calls this. Nodes expose optional extended behaviour
    /// (<c>IContentHashCapability</c>, <c>ISearchCapability</c>, etc.). Capability interfaces are defined
    /// by individual node providers, not in the core library. Decorators override to decide what to forward or block.
    /// </summary>
    T? GetCapability<T>() where T : class => null;
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
