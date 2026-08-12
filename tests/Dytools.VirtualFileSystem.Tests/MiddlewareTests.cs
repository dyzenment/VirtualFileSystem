using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class MiddlewareTests
{
    // -- SymlinkMiddleware -----------------------------------------------------

    [Fact]
    public async Task Symlink_Read_ReturnsTargetContent()
    {
        var node = new SymlinkTestNode();
        node.Store("target.dat", Encoding.UTF8.GetBytes("target-content"));
        node.StoreSymlink("link.lnk", "/s/target.dat");

        var vfs = VfsFactory.Build(b => b
            .Mount("/s", node)
            .UseSymlinks());

        var result = await VfsFactory.ReadTextAsync(vfs, "/s/link.lnk");
        Assert.Equal("target-content", result);
    }

    [Fact]
    public async Task Symlink_GetInfo_IsSymlink_True()
    {
        var node = new SymlinkTestNode();
        node.Store("target.dat", Encoding.UTF8.GetBytes("x"));
        node.StoreSymlink("link.lnk", "/s/target.dat");

        var vfs = VfsFactory.Build(b => b.Mount("/s", node).UseSymlinks());
        var info = await vfs.GetInfoAsync("/s/link.lnk");
        Assert.True(info?.IsSymlink);
    }

    [Fact]
    public async Task Symlink_Delete_RemovesPointerNotTarget()
    {
        var node = new SymlinkTestNode();
        node.Store("target.dat", Encoding.UTF8.GetBytes("keep-me"));
        node.StoreSymlink("link.lnk", "/s/target.dat");

        var vfs = VfsFactory.Build(b => b.Mount("/s", node).UseSymlinks());
        await vfs.DeleteAsync("/s/link.lnk");

        // Target must still be readable
        Assert.Equal("keep-me", await VfsFactory.ReadTextAsync(vfs, "/s/target.dat"));
        // Pointer is gone
        Assert.Null(await vfs.OpenReadAsync("/s/link.lnk"));
    }

    [Fact]
    public async Task Symlink_MaxDepth_ThrowsOnCycle()
    {
        var node = new SymlinkTestNode();
        // a → b → a (cycle)
        node.StoreSymlink("a.lnk", "/s/b.lnk");
        node.StoreSymlink("b.lnk", "/s/a.lnk");

        var vfs = VfsFactory.Build(b => b.Mount("/s", node).UseSymlinks());
        await Assert.ThrowsAsync<InvalidOperationException>(() => vfs.OpenReadAsync("/s/a.lnk"));
    }

    [Fact]
    public async Task Symlink_NonCapableNode_IsSkipped()
    {
        // InMemoryKvNode does NOT implement ISymlinkCapableNode - GetInfoAsync
        // should never be called by SymlinkMiddleware on it.
        var tracker = new CallTrackingNode(new Dytools.VirtualFileSystem.Nodes.InMemory.InMemoryKvNode());
        var vfs = VfsFactory.Build(b => b.Mount("/t", tracker).UseSymlinks());

        await VfsFactory.WriteTextAsync(vfs, "/t/file.txt", "no-symlink");
        var getInfoCallsBefore = tracker.GetInfoCallCount;
        await vfs.OpenReadAsync("/t/file.txt");

        // GetInfo should NOT have been called by the middleware (node isn't symlink-capable)
        Assert.Equal(getInfoCallsBefore, tracker.GetInfoCallCount);
    }

    [Fact]
    public async Task Symlink_CrossMount_FollowsToOtherNode()
    {
        var nodeA = new SymlinkTestNode();
        var nodeB = new Dytools.VirtualFileSystem.Nodes.InMemory.InMemoryKvNode();

        nodeA.StoreSymlink("link.lnk", "/b/target.txt");

        var vfs = VfsFactory.Build(b => b
            .Mount("/a", nodeA)
            .Mount("/b", nodeB)
            .UseSymlinks());

        await VfsFactory.WriteTextAsync(vfs, "/b/target.txt", "cross-mount");
        var result = await VfsFactory.ReadTextAsync(vfs, "/a/link.lnk");
        Assert.Equal("cross-mount", result);
    }

    // -- Custom middleware -----------------------------------------------------

    [Fact]
    public async Task CustomMiddleware_CanInterceptRead()
    {
        var intercepted = false;
        var vfs = VfsFactory.Build(b => b
            .Mount("/a", new Dytools.VirtualFileSystem.Nodes.InMemory.InMemoryKvNode())
            .Use(new LambdaMiddleware(async (ctx, next, ct) =>
            {
                intercepted = true;
                return await next(ctx, ct);
            })));

        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "hi");
        await vfs.OpenReadAsync("/a/file.txt");
        Assert.True(intercepted);
    }

    [Fact]
    public async Task CustomMiddleware_CanShortCircuit()
    {
        var shortCircuitContent = "intercepted!"u8.ToArray();
        var vfs = VfsFactory.Build(b => b
            .Mount("/a", new Dytools.VirtualFileSystem.Nodes.InMemory.InMemoryKvNode())
            .Use(new LambdaMiddleware((ctx, next, ct) =>
                Task.FromResult<Stream?>(new MemoryStream(shortCircuitContent, false)))));

        // Even though no file was written, the middleware returns content
        var result = await VfsFactory.ReadTextAsync(vfs, "/a/file.txt");
        Assert.Equal("intercepted!", result);
    }

    [Fact]
    public async Task MiddlewareOrder_IsRegistrationOrder()
    {
        var log = new List<string>();
        var vfs = VfsFactory.Build(b => b
            .Mount("/a", new Dytools.VirtualFileSystem.Nodes.InMemory.InMemoryKvNode())
            .Use(new LambdaMiddleware(async (ctx, next, ct) =>
            {
                log.Add("first");
                var r = await next(ctx, ct);
                log.Add("first-out");
                return r;
            }))
            .Use(new LambdaMiddleware(async (ctx, next, ct) =>
            {
                log.Add("second");
                var r = await next(ctx, ct);
                log.Add("second-out");
                return r;
            })));

        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "x");
        await vfs.OpenReadAsync("/a/file.txt");

        Assert.Equal(["first", "second", "second-out", "first-out"], log);
    }
}

// -- Test support types --------------------------------------------------------

// Node that recognises files stored with StoreSymlink() as VFS symlinks.
internal sealed class SymlinkTestNode : VfsNodeBase, ISymlinkCapableNode
{
    private readonly ConcurrentDictionary<string, byte[]>  _data     = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string>  _symlinks = new(StringComparer.OrdinalIgnoreCase);

    public void Store(string relativePath, byte[] content)
        => _data[relativePath.TrimStart('/')] = content;

    public void StoreSymlink(string relativePath, string targetVfsPath)
        => _symlinks[relativePath.TrimStart('/')] = targetVfsPath;

    public override Task<Stream?> OpenReadAsync(VfsNodeRequest req, CancellationToken ct = default)
    {
        var key = new string(req.Path.PathSpan);
        return _data.TryGetValue(key, out var bytes)
            ? Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false))
            : Task.FromResult<Stream?>(null);
    }

    public override Task<Stream> OpenWriteAsync(VfsNodeRequest req, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
        => throw new NotSupportedException();

    public override Task DeleteAsync(VfsNodeRequest req, CancellationToken ct = default)
    {
        var key = new string(req.Path.PathSpan);
        _data.TryRemove(key, out _);
        _symlinks.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest req, CancellationToken ct = default)
    {
        var key = new string(req.Path.PathSpan);
        IReadOnlyDictionary<string, object> props = ImmutableDictionary<string, object>.Empty;

        if (_symlinks.TryGetValue(key, out var target))
            props = new Dictionary<string, object> { [VfsPropertyKeys.SymlinkTarget] = target };
        else if (!_data.ContainsKey(key))
            return Task.FromResult<VfsNodeInfo?>(null);

        return Task.FromResult<VfsNodeInfo?>(new VfsNodeInfo
        {
            RelativePath = req.Path,
            IsFile       = true,
            IsDirectory  = false,
            Properties   = props,
        });
    }

    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield break;
    }
}

// Proxy node that counts GetInfoAsync calls (for verifying middleware skips).
internal sealed class CallTrackingNode(IVfsNode inner) : VfsNodeBase
{
    public int GetInfoCallCount { get; private set; }

    public override Task<Stream?> OpenReadAsync(VfsNodeRequest req, CancellationToken ct = default)
        => inner.OpenReadAsync(req, ct);

    public override Task<Stream> OpenWriteAsync(VfsNodeRequest req, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
        => inner.OpenWriteAsync(req, mode, ct);

    public override Task DeleteAsync(VfsNodeRequest req, CancellationToken ct = default)
        => inner.DeleteAsync(req, ct);

    public override Task<bool> ExistsAsync(VfsNodeRequest req, CancellationToken ct = default)
        => inner.ExistsAsync(req, ct);

    public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest req, CancellationToken ct = default)
    {
        GetInfoCallCount++;
        return inner.GetInfoAsync(req, ct);
    }

    public override IAsyncEnumerable<VfsNodeInfo> ListAsync(VfsNodeRequest req, CancellationToken ct = default)
        => inner.ListAsync(req, ct);
}

// Minimal middleware implementation backed by a lambda.
internal sealed class LambdaMiddleware(
    Func<VfsContext, Func<VfsContext, CancellationToken, Task<Stream?>>, CancellationToken, Task<Stream?>> onRead)
    : IVfsMiddleware
{
    public Task<Stream?> InvokeReadAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<Stream?>> next, CancellationToken ct)
        => onRead(ctx, next, ct);

    public Task<Stream> InvokeWriteAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<Stream>> next, CancellationToken ct)
        => next(ctx, ct);
}
