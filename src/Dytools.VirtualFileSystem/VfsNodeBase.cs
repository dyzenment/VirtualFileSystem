using System.Runtime.CompilerServices;
using Dytools.VirtualFileSystem.Internal;

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

    // -- Listing ---------------------------------------------------------------
    //
    // Nodes implement the single-level primitive; the base composes recursion, name
    // matching, and kind/hidden filtering over it. A node that can honor VfsListOptions
    // natively (e.g. LocalFs via EnumerationOptions) may override ListAsync instead.

    // Immediate children of request.Path, with mount-relative RelativePaths. No filtering
    // or recursion - the base applies those.
    protected abstract IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(VfsNodeRequest request, CancellationToken ct);

    // True when this backend's names compare case-sensitively (S3/Azure keys). Governs the
    // client-side search-pattern matcher. Default: case-insensitive (Windows/Mac local, etc.).
    protected virtual bool IsCaseSensitive => false;

    // True when honoring options here would force a full backend enumeration the node cannot
    // push down (e.g. a suffix pattern on a flat object store). Drives ThrowIfPatternNotSupported.
    // Default false: local / in-memory / catalog-backed listings are cheap to scan.
    protected virtual bool RequiresFullScan(VfsListOptions options) => false;

    // A search pattern a prefix-only backend (S3/Azure) can push down: a literal run with at
    // most a single trailing '*' (e.g. "report*"). "*.pdf" and "a*b" are not. Provider nodes
    // use this to implement RequiresFullScan.
    protected static bool IsPurePrefixPattern(string? searchPattern) => VfsGlob.IsPurePrefix(searchPattern);

    // The listing engine: recursion + SearchPattern + Kind + IncludeHidden over the node's
    // single-level primitive. Correct for every backend; capable nodes override to push down.
    public virtual async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest request, VfsListOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        options ??= VfsListOptions.Default;

        if (options.ThrowIfPatternNotSupported && RequiresFullScan(options))
            throw new NotSupportedException(
                $"This node cannot satisfy the requested listing ('{options.SearchPattern}') without a full " +
                "scan; it was rejected because ThrowIfPatternNotSupported is set.");

        var glob = VfsGlob.Compile(options.SearchPattern, IsCaseSensitive);
        // depth is the level of the entries a call yields: the listed directory's immediate
        // children are level 1, their children level 2, etc. MaxDepth bounds that level.
        await foreach (var info in ListFilteredAsync(request, options, glob, depth: 1, ct))
            yield return info;
    }

    private async IAsyncEnumerable<VfsNodeInfo> ListFilteredAsync(
        VfsNodeRequest request, VfsListOptions options, VfsGlob glob, int depth,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var child in ListDirectoryAsync(request, ct))
        {
            ct.ThrowIfCancellationRequested();

            var kindOk = child.IsDirectory
                ? (options.Kind & VfsEntryKind.Directories) != 0
                : (options.Kind & VfsEntryKind.Files) != 0;
            if (kindOk && (options.IncludeHidden || !child.IsHidden) && glob.IsMatch(child.RelativePath.NameSpan))
                yield return child;

            // Recurse into a level-`depth` directory to reach level `depth + 1`, if allowed.
            if (options.Recurse && child.IsDirectory
                && (options.MaxDepth is null || depth < options.MaxDepth))
            {
                var childReq = new VfsNodeRequest(child.RelativePath, request.Mount, request.CallContext);
                await foreach (var descendant in ListFilteredAsync(childReq, options, glob, depth + 1, ct))
                    yield return descendant;
            }
        }
    }

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
