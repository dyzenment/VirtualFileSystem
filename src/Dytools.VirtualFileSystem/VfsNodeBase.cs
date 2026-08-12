using System.Runtime.CompilerServices;

namespace Dytools.VirtualFileSystem;

// Inherit this for all node implementations.
// All abstract methods receive VfsNodeRequest - use request.Path.PathSpan
// for storage lookups; StreamSpan and QuerySpan are available for nodes
// that understand ADS or query-style parameters.
//
// CopyAsync and MoveAsync provide correct stream-based fallbacks.
// Override them when native operations are cheaper (S3 CopyObject, File.Move, etc.).
public abstract class VfsNodeBase : IVfsNode
{
    public abstract Task<Stream?>       OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default);
    public abstract Task<Stream>        OpenWriteAsync(VfsNodeRequest request, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default);
    public abstract Task                DeleteAsync(VfsNodeRequest request, CancellationToken ct = default);
    public abstract Task<VfsNodeInfo?>  GetInfoAsync(VfsNodeRequest request, CancellationToken ct = default);
    public abstract IAsyncEnumerable<VfsNodeInfo> ListAsync(VfsNodeRequest request, CancellationToken ct = default);

    public virtual async Task<bool> ExistsAsync(VfsNodeRequest request, CancellationToken ct = default)
        => await GetInfoAsync(request, ct) is not null;

    // Default: read source → write destination.
    // Override for native same-node copy (S3 CopyObject, Azure CopyBlob, File.Copy).
    public virtual async Task CopyAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        await using var r = await OpenReadAsync(src, ct)
            ?? throw new FileNotFoundException($"VFS copy source not found: {VfsPath.From(src.Mount, src.Path)}");
        await using var w = await OpenWriteAsync(dst, VfsWriteMode.Create, ct);
        await r.CopyToAsync(w, ct);
    }

    // Default: CopyAsync (may itself be overridden) + DeleteAsync.
    // Override for atomic rename (File.Move, SharePoint move API).
    public virtual async Task MoveAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        await CopyAsync(src, dst, ct);
        await DeleteAsync(src, ct);
    }

    // Default: constructs a same-parent dst path from src + newName, then calls MoveAsync.
    // Override for native in-place rename when cheaper than copy+delete.
    public virtual Task RenameAsync(VfsNodeRequest src, string newName, CancellationToken ct = default)
    {
        var dstRelPath = src.Path.WithName(newName);
        return MoveAsync(src, new VfsNodeRequest(dstRelPath, src.Mount, src.CallContext), ct);
    }

    // Consumer escape hatch. Base returns `this as T` - so any node that implements
    // a capability interface exposes it automatically.
    // Decorators override to forward or block specific capabilities.
    // Example: EncryptionNode blocks IContentHashCapability (hash of ciphertext is meaningless).
    public virtual T? GetCapability<T>() where T : class => this as T;
}
