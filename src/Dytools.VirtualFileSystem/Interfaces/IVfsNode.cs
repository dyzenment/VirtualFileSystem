namespace Dytools.VirtualFileSystem;

public interface IVfsNode
{
    Task<Stream?>    OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default);
    Task<Stream>     OpenWriteAsync(VfsNodeRequest request, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default);
    Task             DeleteAsync(VfsNodeRequest request, CancellationToken ct = default);
    Task             CopyAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default);
    Task             MoveAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default);

    // Rename: same-parent, same-node rename. newName is a bare filename with no path separators.
    // Nodes that support a native in-place rename (e.g. File.Move, S3 CopyObject+Delete in one
    // atomic op) should override this. The default in VfsNodeBase falls back to MoveAsync.
    Task             RenameAsync(VfsNodeRequest src, string newName, CancellationToken ct = default);

    // options is never null when called through the pipeline (VfsListOptions.Default at minimum).
    IAsyncEnumerable<VfsNodeInfo> ListAsync(VfsNodeRequest request, VfsListOptions options, CancellationToken ct = default);
    Task<bool>       ExistsAsync(VfsNodeRequest request, CancellationToken ct = default);
    Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest request, CancellationToken ct = default);

    // Consumer escape hatch - the core NEVER calls this.
    // Nodes expose optional extended behaviour (IContentHashCapability, ISearchCapability, etc.).
    // Capability interfaces are defined by individual node providers, not in the core library.
    // Decorators override to decide what to forward or block.
    T? GetCapability<T>() where T : class => null;
}

public enum VfsWriteMode
{
    /// <summary>Truncate any existing content and write from the beginning. Creates the entry if absent.</summary>
    Create,

    /// <summary>Seek to the end of any existing content and append. Creates the entry if absent.</summary>
    Append,

    /// <summary>Fail with <see cref="IOException"/> if the entry already exists.</summary>
    CreateNew,
}
