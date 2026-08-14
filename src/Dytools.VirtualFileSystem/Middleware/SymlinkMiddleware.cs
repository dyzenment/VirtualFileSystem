using Dytools.VirtualFileSystem.Internal;

namespace Dytools.VirtualFileSystem.Middleware;

// Follows node-level symlinks during Read, Write, and GetInfo operations.
//
// A node signals a symlink by returning VfsPropertyKeys.SymlinkTarget in
// VfsNodeInfo.Properties from GetInfoAsync. The value is the absolute VFS path
// of the target. This middleware peeks at node info and, if a target is present,
// calls ctx.Reroute() to redirect the context before passing to next().
//
// PERFORMANCE - skipping nodes that never produce symlinks:
//   By default this middleware only calls GetInfoAsync on nodes that implement
//   ISymlinkCapableNode. Nodes that do not implement it are passed through
//   without any additional I/O. To also enable symlink checking on specific
//   types that cannot implement ISymlinkCapableNode (e.g. third-party nodes),
//   pass their types to the constructor:
//       new SymlinkMiddleware(typeof(ThirdPartyNode))
//
// STREAM CACHING - avoiding double-open in magic-header nodes:
//   Some nodes must open a stream to detect a symlink (magic header approach).
//   To avoid opening the stream again for the actual Read, nodes can store a
//   seekable stream in request.CallContext[VfsPropertyKeys.CachedReadStream]
//   during GetInfoAsync. This middleware returns the cached stream directly from
//   InvokeReadAsync instead of calling next() - exactly one I/O per read.
//
// SYMLINK SEMANTICS (mirrors Unix):
//   Read    - follows the symlink (reads from target)
//   Write   - follows the symlink (writes through to target)
//   GetInfo - follows the symlink (reports target metadata)
//   Delete  - does NOT follow (deletes the pointer file, not the target)
//   Exists  - does NOT follow (checks whether the pointer file itself exists)
//   List    - does NOT follow (lists the directory containing the pointer)
//
// Register via IVfsBuilder.UseSymlinks() or .Use(new SymlinkMiddleware()).
public sealed class SymlinkMiddleware : IVfsMiddleware
{
    private readonly IReadOnlySet<Type>? _extraNodeTypes;
    private const    int                 MaxDepth = 20;

    // Default: only check nodes that implement ISymlinkCapableNode.
    public SymlinkMiddleware() { }

    // Also enable symlink checking for specific node types that you cannot
    // modify to implement ISymlinkCapableNode (e.g. third-party nodes).
    public SymlinkMiddleware(params Type[] extraNodeTypes)
        => _extraNodeTypes = extraNodeTypes.Length > 0 ? new HashSet<Type>(extraNodeTypes) : null;

    public async Task<Stream?> InvokeReadAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<Stream?>> next,
        CancellationToken ct)
    {
        await FollowAsync(ctx, ct);

        // Call next() - the node's OpenReadAsync will check request.CallContext for
        // VfsPropertyKeys.CachedReadStream and return its own pre-opened stream if
        // it stored one during GetInfoAsync. The middleware does not touch or return
        // the stream itself; it only disposes it (in FollowAsync) when a symlink
        // is followed, since the stream would then belong to the wrong file.
        return await next(ctx, ct);
    }

    public async Task<Stream> InvokeWriteAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<Stream>> next,
        CancellationToken ct)
    {
        await FollowAsync(ctx, ct);
        return await next(ctx, ct);
    }

    public async Task<VfsNodeInfo?> InvokeGetInfoAsync(
        VfsContext ctx,
        Func<VfsContext, CancellationToken, Task<VfsNodeInfo?>> next,
        CancellationToken ct)
    {
        await FollowAsync(ctx, ct);
        return await next(ctx, ct);
    }

    // Delete, Copy, Move, Exists, List - inherit transparent pass-through.
    // These operations act on the symlink pointer itself, not the target.

    private async Task FollowAsync(VfsContext ctx, CancellationToken ct)
    {
        // Loop until the resolved node is not a symlink or is not symlink-capable.
        // Each iteration follows one hop; depth counts total hops this call.
        for (var depth = 0; ; depth++)
        {
            if (!IsSymlinkCapable(ctx.ResolvedNode)) return;

            if (depth >= MaxDepth)
                throw new InvalidOperationException(
                    $"Symlink depth limit ({MaxDepth}) exceeded at: {ctx.Path.ToString()}");

            var info = await ctx.ResolvedNode.GetInfoAsync(ctx.BuildNodeRequest(), ct);
            if (info?.Properties.TryGetValue(VfsPropertyKeys.SymlinkTarget, out var raw) != true)
                return;

            // It is a symlink - discard any cached stream (it's from the pointer file,
            // not the target) and follow the link.
            if (ctx.RawItems?.TryGetValue(VfsPropertyKeys.CachedReadStream, out var stale) == true && stale is Stream s)
            {
                ctx.Items.Remove(VfsPropertyKeys.CachedReadStream);
                await s.DisposeAsync();
            }

            ctx.Items[VfsContextKeys.SymlinkDepth]   = depth + 1;
            ctx.Items[VfsContextKeys.SymlinkFollowed] = true;
            // Symlink target is a string stored in Properties - wrap at the boundary.
            ctx.Reroute(VfsPath.From(raw!));
            // continue loop - check whether the target is also a symlink
        }
    }

    private bool IsSymlinkCapable(IVfsNode node)
        => node.GetCapability<ISymlinkCapableNode>() is not null
        || (_extraNodeTypes?.Contains(node.GetType()) == true);
}
