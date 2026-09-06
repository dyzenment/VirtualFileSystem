using System.Text;
using Dytools.VirtualFileSystem.Nodes.LocalFs;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class ReadOptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfs-readopts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // A node whose streams are forward-only, the way the HTTP-backed nodes behave.
    private sealed class ForwardOnlyNode(IVfsNode inner) : VfsNodeBase
    {
        public override async Task<Stream?> OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default)
        {
            var stream = await inner.OpenReadAsync(request, ct);
            return stream is null ? null : new ForwardOnly(stream);
        }

        public override Task<Stream> OpenWriteAsync(VfsNodeRequest r, VfsWriteOptions? o = null, CancellationToken ct = default)
            => inner.OpenWriteAsync(r, o, ct);
        public override Task DeleteAsync(VfsNodeRequest r, VfsDeleteOptions? o = null, CancellationToken ct = default)
            => inner.DeleteAsync(r, o, ct);
        public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest r, CancellationToken ct = default)
            => inner.GetInfoAsync(r, ct);
        protected override IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(VfsNodeRequest r, CancellationToken ct)
            => inner.ListAsync(r, VfsListOptions.Default, ct);

        private sealed class ForwardOnly(Stream inner) : Stream
        {
            public override bool CanRead  => true;
            public override bool CanSeek  => false;
            public override bool CanWrite => false;
            public override long Length   => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int  Read(byte[] b, int o, int c)   => inner.Read(b, o, c);
            public override int  Read(Span<byte> d)             => inner.Read(d);
            public override long Seek(long o, SeekOrigin s)     => throw new NotSupportedException();
            public override void SetLength(long v)              => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c)  => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        }
    }

    private IVirtualFileSystem CreateForwardOnly()
        => VfsFactory.Build(b => b.Mount("/wire", new ForwardOnlyNode(new Nodes.InMemory.InMemoryKvNode())));

    private IVirtualFileSystem CreateLocal()
    {
        Directory.CreateDirectory(_root);
        return VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(_root)));
    }

    [Fact]
    public async Task WithoutOptions_TheNodesOwnStreamComesBack()
    {
        var vfs = CreateForwardOnly();
        await vfs.WriteStringAsync("/wire/a.txt", "payload");

        await using var stream = await vfs.OpenReadAsync("/wire/a.txt");

        Assert.NotNull(stream);
        Assert.False(stream.CanSeek);
    }

    [Fact]
    public async Task Seekable_WrapsAForwardOnlyStream()
    {
        var vfs = CreateForwardOnly();
        await vfs.WriteStringAsync("/wire/a.txt", "ABCDEFGHIJ");

        await using var stream = await vfs.OpenReadAsync("/wire/a.txt", VfsReadOptions.AsSeekable());

        Assert.NotNull(stream);
        Assert.True(stream.CanSeek);
        Assert.Equal(10, stream.Length);

        stream.Position = 4;
        var buf = new byte[3];
        var n = stream.Read(buf, 0, 3);
        Assert.Equal("EFG", Encoding.ASCII.GetString(buf, 0, n));
    }

    [Fact]
    public async Task Seekable_LeavesAnAlreadySeekableStreamAlone()
    {
        var vfs = CreateLocal();
        await vfs.WriteStringAsync("/local/a.txt", "payload");

        await using var stream = await vfs.OpenReadAsync("/local/a.txt", VfsReadOptions.AsSeekable());

        Assert.True(stream!.CanSeek);
        Assert.IsNotType<SeekableReadStream>(stream);   // no wrapper where the node already delivers
    }

    [Fact]
    public async Task Seekable_MissingEntry_StillReturnsNull()
    {
        var vfs = CreateForwardOnly();
        Assert.Null(await vfs.OpenReadAsync("/wire/missing.txt", VfsReadOptions.AsSeekable()));
    }

    [Fact]
    public async Task DisposingTheWrapper_DisposesTheNodesStream()
    {
        var vfs = CreateLocal();
        await vfs.WriteStringAsync("/local/a.txt", "payload");

        // The local node is seekable, so force the wrapper by going through the stream directly.
        var inner = await vfs.OpenReadAsync("/local/a.txt");
        await using (var wrapped = new SeekableReadStream(inner!, ownsInner: true)) { _ = wrapped.Length; }

        Assert.False(inner!.CanRead);
    }

    [Fact]
    public async Task MemoryThreshold_IsHonoured()
    {
        var vfs = CreateForwardOnly();
        var payload = new byte[32 * 1024];
        Random.Shared.NextBytes(payload);
        await vfs.WriteAllBytesAsync("/wire/big.bin", payload);

        await using var stream = await vfs.OpenReadAsync(
            "/wire/big.bin", VfsReadOptions.AsSeekable(memoryThreshold: 4 * 1024));

        var seekable = Assert.IsType<SeekableReadStream>(stream);
        var sink = new MemoryStream();
        await seekable.CopyToAsync(sink);

        Assert.True(seekable.HasSpilledToDisk);
        Assert.Equal(payload, sink.ToArray());
    }

    [Fact]
    public async Task ReadOptions_ReachMiddleware()
    {
        VfsReadOptions? seen = null;
        var vfs = VfsFactory.Build(b => b
            .Mount("/wire", new ForwardOnlyNode(new Nodes.InMemory.InMemoryKvNode()))
            .Use(new CaptureReadOptionsMiddleware(o => seen = o)));

        await vfs.WriteStringAsync("/wire/a.txt", "payload");
        await using var _ = await vfs.OpenReadAsync("/wire/a.txt", VfsReadOptions.AsSeekable());

        Assert.NotNull(seen);
        Assert.True(seen.Seekable);
    }

    private sealed class CaptureReadOptionsMiddleware(Action<VfsReadOptions?> capture) : IVfsMiddleware
    {
        public Task<Stream?> InvokeReadAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<Stream?>> next, CancellationToken ct)
        {
            capture(ctx.ReadOptions);
            return next(ctx, ct);
        }
        public Task<Stream> InvokeWriteAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<Stream>> next, CancellationToken ct) => next(ctx, ct);
        public Task InvokeDeleteAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task> next, CancellationToken ct) => next(ctx, ct);
        public Task InvokeCopyAsync(VfsContext src, VfsContext dst, Func<VfsContext, VfsContext, CancellationToken, Task> next, CancellationToken ct) => next(src, dst, ct);
        public Task InvokeMoveAsync(VfsContext src, VfsContext dst, Func<VfsContext, VfsContext, CancellationToken, Task> next, CancellationToken ct) => next(src, dst, ct);
        public Task InvokeRenameAsync(VfsContext ctx, string newName, Func<VfsContext, string, CancellationToken, Task> next, CancellationToken ct) => next(ctx, newName, ct);
        public Task<bool> InvokeExistsAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<bool>> next, CancellationToken ct) => next(ctx, ct);
        public Task<VfsNodeInfo?> InvokeGetInfoAsync(VfsContext ctx, Func<VfsContext, CancellationToken, Task<VfsNodeInfo?>> next, CancellationToken ct) => next(ctx, ct);
        public IAsyncEnumerable<VfsNodeInfo> InvokeListAsync(VfsContext ctx, Func<VfsContext, CancellationToken, IAsyncEnumerable<VfsNodeInfo>> next, CancellationToken ct) => next(ctx, ct);
    }
}
