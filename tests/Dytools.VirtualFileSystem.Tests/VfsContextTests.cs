using Dytools.VirtualFileSystem.Nodes.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Extensions;

namespace Dytools.VirtualFileSystem.Tests;

// -- SmallBag (via VfsContext.Items in a real pipeline) -----------------------
// SmallBag is internal - exercised through the public VfsContext.Items surface.

public class SmallBagTests
{
    // Middleware that writes to ctx.Items and captures it for assertions.
    private sealed class ItemsCapture(Action<IDictionary<string, object>> capture) : IVfsMiddleware
    {
        public Task<Stream?> InvokeReadAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<Stream?>> next, CancellationToken ct) { capture(ctx.Items); return next(ctx, ct); }
        public Task<Stream> InvokeWriteAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<Stream>> next, CancellationToken ct) { capture(ctx.Items); return next(ctx, ct); }
        public Task InvokeDeleteAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task> next, CancellationToken ct) { capture(ctx.Items); return next(ctx, ct); }
        public Task InvokeCopyAsync(VfsContext src, VfsContext dst, Func<VfsContext, VfsContext, CancellationToken, Task> next, CancellationToken ct) { capture(src.Items); return next(src, dst, ct); }
        public Task InvokeMoveAsync(VfsContext src, VfsContext dst, Func<VfsContext, VfsContext, CancellationToken, Task> next, CancellationToken ct) { capture(src.Items); return next(src, dst, ct); }
        public Task InvokeRenameAsync(VfsContext ctx, string newName, Func<VfsContext, string, CancellationToken, Task> next, CancellationToken ct) { capture(ctx.Items); return next(ctx, newName, ct); }
        public Task<bool> InvokeExistsAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<bool>> next, CancellationToken ct) { capture(ctx.Items); return next(ctx, ct); }
        public Task<VfsNodeInfo?> InvokeGetInfoAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<VfsNodeInfo?>> next, CancellationToken ct) { capture(ctx.Items); return next(ctx, ct); }
        public IAsyncEnumerable<VfsNodeInfo> InvokeListAsync(VfsContext ctx, Func<VfsContext, CancellationToken, IAsyncEnumerable<VfsNodeInfo>> next, CancellationToken ct) { capture(ctx.Items); return next(ctx, ct); }
    }

    [Fact]
    public async Task Items_SetAndGet_SingleItem()
    {
        IDictionary<string, object>? bag = null;
        var capture = new ItemsCapture(items => { items["key"] = "value"; bag = items; });
        var vfs = VfsFactory.Build(b => b.Mount("/a", new InMemoryKvNode()).Use(capture));
        await vfs.ExistsAsync("/a/file.dat");
        Assert.Equal("value", bag!["key"]);
    }

    [Fact]
    public async Task Items_TryGetValue_Missing_ReturnsFalse()
    {
        IDictionary<string, object>? bag = null;
        var capture = new ItemsCapture(items => bag = items);
        var vfs = VfsFactory.Build(b => b.Mount("/a", new InMemoryKvNode()).Use(capture));
        await vfs.ExistsAsync("/a/file.dat");
        Assert.False(bag!.TryGetValue("missing", out _));
    }

    [Fact]
    public async Task Items_KeysAreCaseSensitive()
    {
        IDictionary<string, object>? bag = null;
        var capture = new ItemsCapture(items => { items["Key"] = true; bag = items; });
        var vfs = VfsFactory.Build(b => b.Mount("/a", new InMemoryKvNode()).Use(capture));
        await vfs.ExistsAsync("/a/file.dat");
        Assert.True(bag!.ContainsKey("Key"));
        Assert.False(bag!.ContainsKey("key"));
    }

    [Fact]
    public async Task Items_Remove_ExistingKey()
    {
        IDictionary<string, object>? bag = null;
        var capture = new ItemsCapture(items => { items["x"] = 1; bag = items; });
        var vfs = VfsFactory.Build(b => b.Mount("/a", new InMemoryKvNode()).Use(capture));
        await vfs.ExistsAsync("/a/file.dat");
        Assert.True(bag!.Remove("x"));
        Assert.False(bag!.ContainsKey("x"));
        Assert.Empty(bag!);
    }

    [Fact]
    public async Task Items_Overwrite_UpdatesValue()
    {
        IDictionary<string, object>? bag = null;
        var capture = new ItemsCapture(items => { items["k"] = "first"; items["k"] = "second"; bag = items; });
        var vfs = VfsFactory.Build(b => b.Mount("/a", new InMemoryKvNode()).Use(capture));
        await vfs.ExistsAsync("/a/file.dat");
        Assert.Equal("second", bag!["k"]);
        Assert.Single(bag!);
    }

    [Fact]
    public async Task Items_GrowsBeyondInitialCapacity()
    {
        IDictionary<string, object>? bag = null;
        var capture = new ItemsCapture(items =>
        {
            for (int i = 0; i < 10; i++) items[$"key{i}"] = i;
            bag = items;
        });
        var vfs = VfsFactory.Build(b => b.Mount("/a", new InMemoryKvNode()).Use(capture));
        await vfs.ExistsAsync("/a/file.dat");
        Assert.Equal(10, bag!.Count);
        for (int i = 0; i < 10; i++)
            Assert.Equal(i, bag[$"key{i}"]);
    }

    [Fact]
    public async Task Items_Enumerate_YieldsAllPairs()
    {
        IDictionary<string, object>? bag = null;
        var capture = new ItemsCapture(items => { items["x"] = 1; items["y"] = 2; bag = items; });
        var vfs = VfsFactory.Build(b => b.Mount("/a", new InMemoryKvNode()).Use(capture));
        await vfs.ExistsAsync("/a/file.dat");
        var pairs = bag!.ToList();
        Assert.Equal(2, pairs.Count);
        Assert.Contains(pairs, p => p.Key == "x" && (int)p.Value == 1);
        Assert.Contains(pairs, p => p.Key == "y" && (int)p.Value == 2);
    }
}

// -- VfsCallOptions ------------------------------------------------------------

public class VfsCallOptionsTests
{
    [Fact]
    public void Default_WriteModeIsCreate()
    {
        var opts = default(VfsCallOptions);
        Assert.Equal(VfsWriteMode.Create, opts.WriteMode);
    }

    [Fact]
    public void WithWriteMode_Append()
    {
        var opts = default(VfsCallOptions).WithWriteMode(VfsWriteMode.Append);
        Assert.Equal(VfsWriteMode.Append, opts.WriteMode);
    }

    [Fact]
    public void WithWriteMode_CreateNew()
    {
        var opts = default(VfsCallOptions).WithWriteMode(VfsWriteMode.CreateNew);
        Assert.Equal(VfsWriteMode.CreateNew, opts.WriteMode);
    }

    [Fact]
    public void WithWriteMode_RoundTrip_BackToCreate()
    {
        var opts = default(VfsCallOptions)
            .WithWriteMode(VfsWriteMode.Append)
            .WithWriteMode(VfsWriteMode.Create);
        Assert.Equal(VfsWriteMode.Create, opts.WriteMode);
    }

    [Fact]
    public async Task Pipeline_StampsWriteMode_OnOptions()
    {
        // Verifies that VfsPipeline sets ctx.Options.WriteMode before the chain runs.
        VfsWriteMode? captured = null;
        var middleware = new CaptureModeMiddleware(mode => captured = mode);
        var vfs = VfsFactory.Build(b => b.Mount("/a", new InMemoryKvNode()).Use(middleware));
        await using var s = await vfs.OpenWriteAsync("/a/file.dat", VfsWriteMode.Append);
        Assert.Equal(VfsWriteMode.Append, captured);
    }

    private sealed class CaptureModeMiddleware(Action<VfsWriteMode> capture) : IVfsMiddleware
    {
        public Task<Stream> InvokeWriteAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<Stream>> next, CancellationToken ct)
        {
            capture(ctx.Options.WriteMode);
            return next(ctx, ct);
        }
        public Task<Stream?> InvokeReadAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<Stream?>> next, CancellationToken ct) => next(ctx, ct);
        public Task InvokeDeleteAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task> next, CancellationToken ct) => next(ctx, ct);
        public Task InvokeCopyAsync(VfsContext src, VfsContext dst, Func<VfsContext, VfsContext, CancellationToken, Task> next, CancellationToken ct) => next(src, dst, ct);
        public Task InvokeMoveAsync(VfsContext src, VfsContext dst, Func<VfsContext, VfsContext, CancellationToken, Task> next, CancellationToken ct) => next(src, dst, ct);
        public Task InvokeRenameAsync(VfsContext ctx, string newName, Func<VfsContext, string, CancellationToken, Task> next, CancellationToken ct) => next(ctx, newName, ct);
        public Task<bool> InvokeExistsAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<bool>> next, CancellationToken ct) => next(ctx, ct);
        public Task<VfsNodeInfo?> InvokeGetInfoAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<VfsNodeInfo?>> next, CancellationToken ct) => next(ctx, ct);
        public IAsyncEnumerable<VfsNodeInfo> InvokeListAsync(VfsContext ctx, Func<VfsContext, CancellationToken, IAsyncEnumerable<VfsNodeInfo>> next, CancellationToken ct) => next(ctx, ct);
    }
}
