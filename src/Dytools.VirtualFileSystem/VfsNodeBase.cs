using System.Runtime.CompilerServices;
using Dytools.VirtualFileSystem.Internal;

namespace Dytools.VirtualFileSystem;

/// <summary>
/// Base class to inherit for all node implementations.
/// All abstract methods receive <see cref="VfsNodeRequest"/> - use <c>request.Path.PathSpan</c>
/// for storage lookups; <c>StreamSpan</c> and <c>QuerySpan</c> are available for nodes
/// that understand ADS or query-style parameters.
/// <para>
/// <see cref="CopyAsync"/> and <see cref="MoveAsync"/> provide correct stream-based fallbacks.
/// Override them when native operations are cheaper (S3 CopyObject, <see cref="File.Move(string, string)"/>, etc.).
/// </para>
/// </summary>
public abstract class VfsNodeBase : IVfsNode
{
    /// <summary>Opens the content at the request's path for reading, or returns <c>null</c> if it does not exist.</summary>
    public abstract Task<Stream?>       OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default);
    /// <summary>Opens (or creates) the content at the request's path for writing, honoring the given <see cref="VfsWriteOptions.Mode"/>.</summary>
    public abstract Task<Stream>        OpenWriteAsync(VfsNodeRequest request, VfsWriteOptions? options = null, CancellationToken ct = default);
    /// <summary>
    /// Deletes the file or directory at the request's path, honouring <paramref name="options"/> -
    /// which is never null when called through the pipeline.
    /// <para>
    /// Every implementation should open with
    /// <c>options.ResolveRecycle(available, request.Path)</c>: a backend with no recoverable delete
    /// passes <c>available: false</c> and gets the right throw-or-degrade behaviour for a caller that
    /// asked to recycle, rather than silently destroying what it was asked to keep.
    /// </para>
    /// </summary>
    public abstract Task                DeleteAsync(VfsNodeRequest request, VfsDeleteOptions? options = null, CancellationToken ct = default);
    /// <summary>Returns metadata for the request's path, or <c>null</c> if it does not exist.</summary>
    public abstract Task<VfsNodeInfo?>  GetInfoAsync(VfsNodeRequest request, CancellationToken ct = default);

    // -- Listing ---------------------------------------------------------------
    //
    // Nodes implement the single-level primitive; the base composes recursion, name
    // matching, and kind/hidden filtering over it. A node that can honor VfsListOptions
    // natively (e.g. LocalFs via EnumerationOptions) may override ListAsync instead.

    /// <summary>
    /// Yields the immediate children of <c>request.Path</c>, with mount-relative <c>RelativePath</c>s.
    /// No filtering or recursion - the base applies those.
    /// </summary>
    protected abstract IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(VfsNodeRequest request, CancellationToken ct);

    /// <summary>
    /// <c>true</c> when this backend's names compare case-sensitively (S3/Azure keys). Governs the
    /// client-side search-pattern matcher. Default: case-insensitive (Windows/Mac local, etc.).
    /// </summary>
    protected virtual bool IsCaseSensitive => false;

    /// <summary>
    /// <c>true</c> when honoring <paramref name="options"/> here would force a full backend enumeration
    /// the node cannot push down (e.g. a suffix pattern on a flat object store). Drives
    /// <c>ThrowIfPatternNotSupported</c>. Default <c>false</c>: local / in-memory / catalog-backed
    /// listings are cheap to scan.
    /// </summary>
    protected virtual bool RequiresFullScan(VfsListOptions options) => false;

    /// <summary>
    /// Returns <c>true</c> for a search pattern a prefix-only backend (S3/Azure) can push down: a literal
    /// run with at most a single trailing <c>'*'</c> (e.g. <c>"report*"</c>). <c>"*.pdf"</c> and <c>"a*b"</c>
    /// are not. Provider nodes use this to implement <see cref="RequiresFullScan"/>.
    /// </summary>
    protected static bool IsPurePrefixPattern(string? searchPattern) => VfsGlob.IsPurePrefix(searchPattern);

    /// <summary>
    /// The listing engine: recursion + <c>SearchPattern</c> + <c>Kind</c> + <c>IncludeHidden</c> over the node's
    /// single-level primitive. Correct for every backend; capable nodes override to push down.
    /// </summary>
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

            // A symlink is matched on being a link first: asking for Symlinks alone should return
            // pointers whatever they point at, and asking for Files alone should not sweep them in.
            var kindOk = child.IsSymlink
                ? (options.Kind & VfsEntryKind.Symlinks) != 0
                : child.IsDirectory
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

    /// <summary>Returns <c>true</c> when the request's path exists. Default implementation calls <see cref="GetInfoAsync"/>.</summary>
    public virtual async Task<bool> ExistsAsync(VfsNodeRequest request, CancellationToken ct = default)
        => await GetInfoAsync(request, ct) is not null;

    /// <summary>
    /// Copies <paramref name="src"/> to <paramref name="dst"/>. Default: read source into write destination.
    /// Override for native same-node copy (S3 CopyObject, Azure CopyBlob, <see cref="File.Copy(string, string)"/>).
    /// </summary>
    /// <exception cref="FileNotFoundException">The source does not exist.</exception>
    public virtual async Task CopyAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        await using var r = await OpenReadAsync(src, ct)
            ?? throw new FileNotFoundException($"VFS copy source not found: {VfsPath.From(src.Mount, src.Path)}");
        await using var w = await OpenWriteAsync(dst, VfsWriteMode.Create, ct);
        await r.CopyToAsync(w, ct);
    }

    /// <summary>
    /// Moves <paramref name="src"/> to <paramref name="dst"/>. Default: <see cref="CopyAsync"/> (may itself be
    /// overridden) + <see cref="DeleteAsync"/>. Override for atomic rename (<see cref="File.Move(string, string)"/>,
    /// SharePoint move API).
    /// </summary>
    public virtual async Task MoveAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        await CopyAsync(src, dst, ct);
        // Explicitly permanent. The destination now holds the bytes, so parking the source in a
        // recycle bin would leave a move looking like a delete the user has to go and clean up.
        await DeleteAsync(src, VfsDeleteOptions.Default, ct);
    }

    /// <summary>
    /// Renames <paramref name="src"/> to <paramref name="newName"/>. Default: constructs a same-parent
    /// destination path from <paramref name="src"/> + <paramref name="newName"/>, then calls <see cref="MoveAsync"/>.
    /// Override for native in-place rename when cheaper than copy+delete.
    /// </summary>
    public virtual Task RenameAsync(VfsNodeRequest src, string newName, CancellationToken ct = default)
    {
        var dstRelPath = src.Path.WithName(newName);
        return MoveAsync(src, new VfsNodeRequest(dstRelPath, src.Mount, src.CallContext), ct);
    }

    /// <summary>
    /// Entry-level capability. The base returns null: an entry capability is bound to one entry, which
    /// a node serving every path under its mount cannot be, so a node exposing one has to build it.
    /// Decorators override to forward or block. An encryption node, for instance, should refuse a
    /// hashing capability - a hash of ciphertext answers nothing.
    /// </summary>
    /// <typeparam name="T">The capability interface requested.</typeparam>
    /// <param name="relativePath">The entry, relative to this node's mount.</param>
    /// <returns>The capability bound to that entry, or null if this node does not expose it.</returns>
    public virtual T? GetEntryCapability<T>(VfsPath relativePath) where T : class, IEntryCapability
        => null;

    /// <summary>
    /// Node-level capability. The base returns <c>this as T</c>, so a node implementing the interface
    /// exposes it automatically. Override to forward or block.
    /// </summary>
    /// <typeparam name="T">The capability interface requested.</typeparam>
    /// <param name="mountPoint">The mount prefix this node is serving.</param>
    /// <returns>The capability, or null if this node does not expose it.</returns>
    public virtual T? GetNodeCapability<T>(VfsPath mountPoint) where T : class, INodeCapability
        => this as T;
}
