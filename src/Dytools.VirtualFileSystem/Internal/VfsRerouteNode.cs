using Microsoft.Extensions.DependencyInjection;

namespace Dytools.VirtualFileSystem.Internal;

// A node that forwards every operation to another VFS path - so a backend mounted
// once (e.g. "/azure") can be referenced as the inner node of many decorator mounts
// without reconfiguring it each time. Resolution goes straight to the target's node
// via the registry (skipping the middleware pipeline); alias expansion still applies.
internal sealed class VfsRerouteNode : VfsNodeBase
{
    private readonly IVfsMountRegistry _registry;
    private readonly IServiceProvider  _provider;
    private readonly string            _baseStr;   // normalized absolute base, e.g. "/azure/docs-blobs"

    private string? _baseRel;   // base path relative to its own mount (cached)

    private const string DepthKey   = "vfs.reroute.depth";
    private const int    DepthLimit = 20;

    public VfsRerouteNode(IServiceProvider provider, string basePath)
    {
        _provider = provider;
        _registry = provider.GetRequiredService<IVfsMountRegistry>();
        _baseStr  = VfsPath.From(basePath).ToString();
    }

    // -- Forwarded operations --------------------------------------------------

    public override Task<Stream?> OpenReadAsync(VfsNodeRequest req, CancellationToken ct = default)
    { var (n, r) = Target(req); return n.OpenReadAsync(r, ct); }

    public override Task<Stream> OpenWriteAsync(VfsNodeRequest req, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
    { var (n, r) = Target(req); return n.OpenWriteAsync(r, mode, ct); }

    public override Task DeleteAsync(VfsNodeRequest req, CancellationToken ct = default)
    { var (n, r) = Target(req); return n.DeleteAsync(r, ct); }

    public override async Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest req, CancellationToken ct = default)
    {
        var (n, r) = Target(req);
        var info = await n.GetInfoAsync(r, ct);
        return info is null ? null : info with { RelativePath = Rebase(info.RelativePath) };
    }

    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest req, VfsListOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (n, r) = Target(req);
        await foreach (var info in n.ListAsync(r, options, ct))
            yield return info with { RelativePath = Rebase(info.RelativePath) };
    }

    // Reroute forwards the full options-aware ListAsync above; this satisfies the base
    // primitive and is only reached if something bypasses the override.
    protected override IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(VfsNodeRequest request, CancellationToken ct)
        => ListAsync(request, VfsListOptions.Default, ct);

    public override T? GetCapability<T>() where T : class
    {
        var (n, _) = Target(new VfsNodeRequest(default));
        return n.GetCapability<T>();
    }

    // -- Path translation ------------------------------------------------------

    private (IVfsNode Node, VfsNodeRequest Request) Target(VfsNodeRequest req)
    {
        GuardDepth(req);

        var reqRel  = new string(req.Path.PathSpan);
        var fullStr = reqRel.Length == 0 ? _baseStr : _baseStr + "/" + reqRel;

        var (node, mount, resolved) = _registry.Resolve(VfsPath.From(fullStr), _provider, internalAllowed: true);
        return (node, new VfsNodeRequest(Relative(resolved, mount), mount, req.CallContext));
    }

    // Node-relative slice of `resolved` under `mount` (mirrors VfsContext.BuildNodeRequest).
    private static VfsPath Relative(VfsPath resolved, VfsPath mount)
    {
        var mountLen = mount.Length;
        var span     = resolved.PathSpan;
        var start    = mountLen < span.Length && span[mountLen] == '/' ? mountLen + 1 : mountLen;
        return start < span.Length ? resolved.WithOffset(start) : default;
    }

    // Re-anchor a target-mount-relative path onto this reroute's base, so returned
    // RelativePaths read as if this node were rooted at the base.
    private VfsPath Rebase(VfsPath targetRelative)
    {
        _baseRel ??= new string(Relative(
            _registry.Resolve(VfsPath.From(_baseStr), _provider, internalAllowed: true).ResolvedPath,
            _registry.Resolve(VfsPath.From(_baseStr), _provider, internalAllowed: true).MountPoint).PathSpan);

        if (_baseRel.Length == 0) return targetRelative;
        var s = new string(targetRelative.PathSpan);
        return s.Length <= _baseRel.Length ? VfsPath.From("") : VfsPath.From(s[(_baseRel.Length + 1)..]);
    }

    private static void GuardDepth(VfsNodeRequest req)
    {
        if (req.CallContext is not { } ctx) return;
        var depth = ctx.TryGetValue(DepthKey, out var d) && d is int i ? i : 0;
        if (depth >= DepthLimit)
            throw new InvalidOperationException("VFS reroute depth limit exceeded (cyclic mount reference?).");
        ctx[DepthKey] = depth + 1;
    }
}
