using Dytools.VirtualFileSystem.Internal;

namespace Dytools.VirtualFileSystem;

// Built once per VFS call before the pipeline runs.
// Passed through every middleware in registration order.
//
// Path is changed only via Reroute().
// Reroute() atomically re-resolves ResolvedNode, MountPoint, and ResolvedPath
// so context is always internally consistent.
//
// Items is a shared bag for middleware-to-middleware communication within
// a single call (same pattern as HttpContext.Items). Use typed extension
// methods to avoid stringly-typed key access. See VfsContextKeys for
// well-known keys used by built-in middleware.
//
// Options holds per-operation parameters (WriteMode, future flags) in a
// packed long - no heap allocation. New parameters never require changing
// IVfsMiddleware signatures.
//
// Layout (80 bytes on 64-bit .NET):
//   object header:  16B
//   _registry:       8B
//   ResolvedNode:    8B
//   _items:          8B  (SmallBag, lazy - null in common case)
//   _resolved:      16B  (VfsPath)
//   MountPoint:     16B  (VfsPath)
//   Options:         8B  (VfsCallOptions, long-backed)
public sealed class VfsContext
{
    private readonly IVfsMountRegistry _registry;
    private readonly IServiceProvider? _ambient;   // scope used to resolve scoped/transient nodes

    internal VfsContext(VfsPath path, IVfsMountRegistry registry, IServiceProvider? ambient = null)
    {
        _registry = registry;
        _ambient  = ambient;
        Reroute(path);
    }

    private VfsPath _resolved;
    private SmallBag? _items;

    // The current (possibly rewritten by middleware) VFS path.
    // Always in sync with ResolvedNode - change only via Reroute().
    //
    // Non-aliased (common): _resolved directly - zero alloc.
    // Aliased (rare): reads origin string from _items - _items is already
    //   allocated when an alias is followed (AliasFollowed flag is also stored there).
    public VfsPath Path
        => _items is not null && _items.TryGetValue(VfsContextKeys.AliasOrigin, out var origin)
            ? VfsPath.From((string)origin)
            : _resolved;

    // Path after VFS-level alias expansion (read-only).
    // Differs from Path when an Alias() entry was followed.
    public VfsPath ResolvedPath => _resolved;

    // The mount point prefix that was matched (e.g. "/local/c").
    // Sourced directly from the registry key - no allocation.
    public VfsPath MountPoint { get; private set; }

    // The node responsible for this path. Always in sync with Path.
    public IVfsNode ResolvedNode { get; private set; } = default!;

    // Per-operation options: WriteMode, and future flags (CopyOverwrite, etc.).
    // Stored as a packed long - no heap allocation. Set by VfsPipeline before
    // the chain runs; readable and writable by middleware via ctx.Options.
    public VfsCallOptions Options { get; internal set; }

    // Directory-listing options for a List call (recursion, search pattern, kind, etc.).
    // Set by VfsPipeline before the list chain runs; middleware may read or rewrite it
    // (e.g. force IncludeHidden = false, inject a scoping SearchPattern). Null outside a List.
    public VfsListOptions? ListOptions { get; internal set; }

    // Shared bag for within-call middleware communication.
    // Prefer typed extension methods (GetUser/SetUser) over raw access.
    // Allocated lazily - most calls never touch it.
    private SmallBag ItemsBag => _items ??= new SmallBag();
    public IDictionary<string, object> Items => ItemsBag;

    // Internal fast-path: null when no middleware has written to Items yet.
    internal IDictionary<string, object>? RawItems => _items;

    // Re-resolves when middleware rewrites the path (PathRewriteMiddleware, SymlinkMiddleware, etc.).
    // Also correct for same-mount rewrites (registry lookup stamps the new mount length).
    public void Reroute(VfsPath newPath)
    {
        var (node, mount, resolved) = _registry.Resolve(newPath, _ambient);
        ResolvedNode = node;
        MountPoint   = mount;
        _resolved    = resolved;
        if (resolved != newPath) // alias was followed - resolved differs from the input path
        {
            ItemsBag[VfsContextKeys.AliasOrigin]  = newPath.ToString();
            ItemsBag[VfsContextKeys.AliasFollowed] = true;
        }
        else
        {
            // If a previous Reroute set alias keys, clear them - the latest resolution wins.
            _items?.Remove(VfsContextKeys.AliasOrigin);
            _items?.Remove(VfsContextKeys.AliasFollowed);
        }
    }

    // Convenience: build the VfsNodeRequest for the current context state.
    // Called by VfsPipeline terminals just before dispatching to the node.
    internal VfsNodeRequest BuildNodeRequest()
    {
        var mountLen = MountPoint.Length;
        var fullSpan = _resolved.PathSpan;
        var relStart = mountLen < fullSpan.Length && fullSpan[mountLen] == '/'
            ? mountLen + 1
            : mountLen;
        var relPath = relStart < fullSpan.Length
            ? _resolved.WithOffset(relStart)
            : default;
        return new VfsNodeRequest(relPath, MountPoint, _items);
    }
}
