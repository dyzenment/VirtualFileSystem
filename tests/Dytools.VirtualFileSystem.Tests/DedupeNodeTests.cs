using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Dytools.VirtualFileSystem.Catalog;
using Dytools.VirtualFileSystem.Nodes.Dedupe;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class DedupeNodeTests
{
    // Minimal in-memory blob store used as the inner node, so tests can assert how
    // many distinct blobs were physically stored (i.e. that dedup actually happened).
    private sealed class BlobStoreNode : VfsNodeBase
    {
        private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

        public int BlobCount => _blobs.Count;
        public bool HasKey(string key) => _blobs.ContainsKey(key);

        public override Task<Stream?> OpenReadAsync(VfsNodeRequest req, CancellationToken ct = default)
        {
            var key = new string(req.Path.PathSpan);
            return Task.FromResult<Stream?>(_blobs.TryGetValue(key, out var b) ? new MemoryStream(b, false) : null);
        }

        public override Task<Stream> OpenWriteAsync(VfsNodeRequest req, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
        {
            var key = new string(req.Path.PathSpan);
            return Task.FromResult<Stream>(new CommitMs(bytes => _blobs[key] = bytes));
        }

        public override Task DeleteAsync(VfsNodeRequest req, CancellationToken ct = default)
        {
            _blobs.TryRemove(new string(req.Path.PathSpan), out _);
            return Task.CompletedTask;
        }

        public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest req, CancellationToken ct = default)
        {
            var key = new string(req.Path.PathSpan);
            return Task.FromResult<VfsNodeInfo?>(_blobs.TryGetValue(key, out var b)
                ? new VfsNodeInfo { RelativePath = req.Path, IsFile = true, IsDirectory = false, SizeBytes = b.Length }
                : null);
        }

        protected override async IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(VfsNodeRequest req, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        private sealed class CommitMs(Action<byte[]> commit) : MemoryStream
        {
            private bool _done;
            public override async ValueTask DisposeAsync() { Commit(); await base.DisposeAsync(); }
            protected override void Dispose(bool disposing) { if (disposing) Commit(); base.Dispose(disposing); }
            private void Commit() { if (!_done) { _done = true; commit(ToArray()); } }
        }
    }

    private static VfsNodeRequest Req(string path) => new(VfsPath.From(path));

    private static async Task WriteAsync(DedupeNode node, string path, string text)
    {
        await using var w = await node.OpenWriteAsync(Req(path));
        await w.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    private static async Task<string?> ReadAsync(DedupeNode node, string path)
    {
        await using var r = await node.OpenReadAsync(Req(path));
        if (r is null) return null;
        using var sr = new StreamReader(r);
        return await sr.ReadToEndAsync();
    }

    [Fact]
    public async Task Write_Then_Read_Roundtrips()
    {
        var node = new DedupeNode(new BlobStoreNode(), new InMemoryVfsCatalog());
        await WriteAsync(node, "docs/hello.txt", "Hello, dedupe!");
        Assert.Equal("Hello, dedupe!", await ReadAsync(node, "docs/hello.txt"));
    }

    [Fact]
    public async Task EmptyBlobPrefix_StoresBlobsAtBackingRoot()
    {
        var inner   = new BlobStoreNode();
        var catalog = new InMemoryVfsCatalog();
        var node    = new DedupeNode(inner, catalog, new DedupeOptions { BlobPrefix = "", FanOut = 0 });

        await WriteAsync(node, "docs/hello.txt", "no prefix");
        Assert.Equal("no prefix", await ReadAsync(node, "docs/hello.txt"));   // round-trips

        // The blob is keyed by its content id at the backing root - no ".blobs/" wrapper.
        var id = (await catalog.GetAsync(VfsPath.From("docs/hello.txt")))!.ContentId!;
        Assert.True(inner.HasKey(id));
        Assert.False(inner.HasKey($".blobs/{id}"));
    }

    [Fact]
    public async Task IdenticalContent_StoresOneBlob_RefCountTwo()
    {
        var inner = new BlobStoreNode();
        var catalog = new InMemoryVfsCatalog();
        var node = new DedupeNode(inner, catalog);

        await WriteAsync(node, "a/x.txt", "same bytes");
        await WriteAsync(node, "b/y.txt", "same bytes");

        Assert.Equal(1, inner.BlobCount);   // dedup: only one physical blob
        var id = (await catalog.GetAsync(VfsPath.From("a/x.txt")))!.ContentId!;
        Assert.Equal(2, await catalog.ReferenceCountAsync(id));
        Assert.Equal("same bytes", await ReadAsync(node, "a/x.txt"));
        Assert.Equal("same bytes", await ReadAsync(node, "b/y.txt"));
    }

    [Fact]
    public async Task Copy_IsMetadataOnly_SharesBlob()
    {
        var inner = new BlobStoreNode();
        var node = new DedupeNode(inner, new InMemoryVfsCatalog());

        await WriteAsync(node, "src.txt", "payload");
        await node.CopyAsync(Req("src.txt"), Req("copy.txt"));

        Assert.Equal(1, inner.BlobCount);                 // no new bytes written
        Assert.Equal("payload", await ReadAsync(node, "copy.txt"));
        Assert.Equal("payload", await ReadAsync(node, "src.txt"));
    }

    [Fact]
    public async Task Overwrite_Forks_And_GCsOldBlob()
    {
        var inner = new BlobStoreNode();
        var node = new DedupeNode(inner, new InMemoryVfsCatalog());

        await WriteAsync(node, "f.txt", "version one");
        await WriteAsync(node, "f.txt", "version two");   // overwrite → old blob orphaned

        Assert.Equal(1, inner.BlobCount);                 // old blob GC'd, only new remains
        Assert.Equal("version two", await ReadAsync(node, "f.txt"));
    }

    [Fact]
    public async Task Delete_RemovesBlob_OnLastReference()
    {
        var inner = new BlobStoreNode();
        var node = new DedupeNode(inner, new InMemoryVfsCatalog());

        await WriteAsync(node, "a.txt", "shared");
        await WriteAsync(node, "b.txt", "shared");
        Assert.Equal(1, inner.BlobCount);

        await node.DeleteAsync(Req("a.txt"));
        Assert.Equal(1, inner.BlobCount);                 // b.txt still references it
        Assert.Null(await ReadAsync(node, "a.txt"));

        await node.DeleteAsync(Req("b.txt"));
        Assert.Equal(0, inner.BlobCount);                 // last reference gone → blob deleted
    }

    [Fact]
    public async Task Move_RenamesWithoutTouchingBlobs()
    {
        var inner = new BlobStoreNode();
        var node = new DedupeNode(inner, new InMemoryVfsCatalog());

        await WriteAsync(node, "old/name.txt", "data");
        await node.MoveAsync(Req("old/name.txt"), Req("new/name.txt"));

        Assert.Equal(1, inner.BlobCount);
        Assert.Null(await ReadAsync(node, "old/name.txt"));
        Assert.Equal("data", await ReadAsync(node, "new/name.txt"));
    }

    [Fact]
    public async Task List_ReturnsChildren_FilesAndDirs()
    {
        var node = new DedupeNode(new BlobStoreNode(), new InMemoryVfsCatalog());
        await WriteAsync(node, "dir/a.txt", "a");
        await WriteAsync(node, "dir/b.txt", "b");
        await WriteAsync(node, "dir/sub/c.txt", "c");

        var names = new List<string>();
        await foreach (var info in node.ListAsync(Req("dir"), VfsListOptions.Default))
            names.Add(info.RelativePath.ToString());

        Assert.Contains("dir/a.txt", names);
        Assert.Contains("dir/b.txt", names);
        Assert.Contains("dir/sub", names);       // the subdirectory
        Assert.DoesNotContain("dir/sub/c.txt", names);   // not recursive
    }

    [Fact]
    public async Task Append_ExtendsContent()
    {
        var node = new DedupeNode(new BlobStoreNode(), new InMemoryVfsCatalog());
        await WriteAsync(node, "log.txt", "line1");

        await using (var a = await node.OpenWriteAsync(Req("log.txt"), VfsWriteMode.Append))
            await a.WriteAsync(Encoding.UTF8.GetBytes("+line2"));

        Assert.Equal("line1+line2", await ReadAsync(node, "log.txt"));
    }

    [Fact]
    public async Task CreateNew_Throws_WhenExists()
    {
        var node = new DedupeNode(new BlobStoreNode(), new InMemoryVfsCatalog());
        await WriteAsync(node, "x.txt", "first");
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var _ = await node.OpenWriteAsync(Req("x.txt"), VfsWriteMode.CreateNew);
        });
    }

    [Fact]
    public async Task GetCapability_ExposesCatalog()
    {
        var catalog = new InMemoryVfsCatalog();
        var node = new DedupeNode(new BlobStoreNode(), catalog);
        Assert.Same(catalog, node.GetCapability<IVfsCatalog>());
    }

    [Fact]
    public async Task Entry_RecordsHash_DefaultContentIdEqualsHash()
    {
        var catalog = new InMemoryVfsCatalog();
        var node = new DedupeNode(new BlobStoreNode(), catalog);

        await WriteAsync(node, "f.txt", "hash me");
        var e = (await catalog.GetAsync(VfsPath.From("f.txt")))!;

        Assert.NotNull(e.Hash);
        Assert.Equal(64, e.Hash!.Length);        // sha256 hex
        Assert.Equal(e.Hash, e.ContentId);       // default mode: storage key == hash
    }

    [Fact]
    public async Task ReadableBlobNames_UsesFileName_AsContentId()
    {
        var inner   = new BlobStoreNode();
        var catalog = new InMemoryVfsCatalog();
        var node = new DedupeNode(inner, catalog, new DedupeOptions { ReadableBlobNames = true, FanOut = 0 });

        await WriteAsync(node, "docs/report.pdf", "PDF BYTES");
        var e = (await catalog.GetAsync(VfsPath.From("docs/report.pdf")))!;

        Assert.Equal("report.pdf", e.ContentId);         // storage key is the file name
        Assert.NotEqual(e.ContentId, e.Hash);            // but the hash is recorded separately
        Assert.Equal("PDF BYTES", await ReadAsync(node, "docs/report.pdf"));
    }

    [Fact]
    public async Task ReadableBlobNames_DedupsByHash_ReusesFirstName()
    {
        var inner   = new BlobStoreNode();
        var catalog = new InMemoryVfsCatalog();
        var node = new DedupeNode(inner, catalog, new DedupeOptions { ReadableBlobNames = true, FanOut = 0 });

        await WriteAsync(node, "a/report.pdf",  "SAME");
        await WriteAsync(node, "b/summary.pdf", "SAME");   // identical content, different name

        Assert.Equal(1, inner.BlobCount);                 // dedup by hash → one blob
        Assert.Equal("report.pdf", (await catalog.GetAsync(VfsPath.From("a/report.pdf")))!.ContentId);
        Assert.Equal("report.pdf", (await catalog.GetAsync(VfsPath.From("b/summary.pdf")))!.ContentId);  // reuses the first name
    }

    [Fact]
    public async Task ReadableBlobNames_NameCollision_AppendsSequence()
    {
        var inner   = new BlobStoreNode();
        var catalog = new InMemoryVfsCatalog();
        var node = new DedupeNode(inner, catalog, new DedupeOptions { ReadableBlobNames = true, FanOut = 0 });

        await WriteAsync(node, "a/report.pdf", "CONTENT ONE");
        await WriteAsync(node, "b/report.pdf", "CONTENT TWO");   // same name, different content

        Assert.Equal(2, inner.BlobCount);
        Assert.Equal("report.pdf",   (await catalog.GetAsync(VfsPath.From("a/report.pdf")))!.ContentId);
        Assert.Equal("report-2.pdf", (await catalog.GetAsync(VfsPath.From("b/report.pdf")))!.ContentId);
        Assert.Equal("CONTENT ONE", await ReadAsync(node, "a/report.pdf"));
        Assert.Equal("CONTENT TWO", await ReadAsync(node, "b/report.pdf"));
    }
}
