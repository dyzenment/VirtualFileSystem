using Dytools.VirtualFileSystem.Catalog;
using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class JsonFileVfsCatalogTests
{
    private static VfsPath P(string s) => VfsPath.From(s);

    private static CatalogEntry File(string path, string contentId, long size = 1) => new()
    {
        Path        = P(path),
        IsDirectory = false,
        ContentId   = contentId,
        Hash        = contentId,
        Size        = size,
        ModifiedAt  = DateTimeOffset.UnixEpoch,
        CreatedAt   = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Put_Get_List_Roundtrip()
    {
        var cat = new JsonFileVfsCatalog(new InMemoryKvNode());
        await cat.PutEntryAsync(File("docs/a.txt", "h1"));
        await cat.PutEntryAsync(File("docs/b.txt", "h2"));

        Assert.Equal("h1", (await cat.GetAsync(P("docs/a.txt")))!.ContentId);

        var names = new List<string>();
        await foreach (var e in cat.ListChildrenAsync(P("docs"))) names.Add(e.Path.ToString());
        Assert.Contains("docs/a.txt", names);
        Assert.Contains("docs/b.txt", names);
    }

    [Fact]
    public async Task PutEntries_CoalescesToOneWrite_AndAppliesAll()
    {
        var store = new WriteCountingNode();
        var cat   = new JsonFileVfsCatalog(store);
        await cat.PutEntryAsync(File("seed.txt", "s"));   // establish a baseline write count
        var baseline = store.Writes;

        await cat.PutEntriesAsync(Enumerable.Range(0, 50).Select(i => File($"docs/f{i}.txt", $"h{i}")));

        Assert.Equal(baseline + 1, store.Writes);   // 50 entries persisted with a single document write
        for (var i = 0; i < 50; i++)
            Assert.Equal($"h{i}", (await cat.GetAsync(P($"docs/f{i}.txt")))!.ContentId);
        // Survives reload from the same store.
        Assert.Equal("h49", (await new JsonFileVfsCatalog(store).GetAsync(P("docs/f49.txt")))!.ContentId);
    }

    [Fact]
    public async Task BulkRemove_CoalescesToOneWrite()
    {
        var store = new WriteCountingNode();
        var cat   = new JsonFileVfsCatalog(store);
        await cat.PutEntriesAsync(Enumerable.Range(0, 10).Select(i => File($"docs/f{i}.txt", $"h{i}")));
        var baseline = store.Writes;

        await cat.RemoveAsync(Enumerable.Range(0, 10).Select(i => P($"docs/f{i}.txt")));

        Assert.Equal(baseline + 1, store.Writes);   // 10 removals persisted with a single document write
        Assert.Null(await cat.GetAsync(P("docs/f0.txt")));
        Assert.Null(await cat.GetAsync(P("docs/f9.txt")));
    }

    // Wraps an InMemoryKvNode and counts document writes (each JsonFileVfsCatalog save is one
    // OpenWrite of the temp file). Only the members the catalog touches are forwarded.
    private sealed class WriteCountingNode : VfsNodeBase
    {
        private readonly InMemoryKvNode _inner = new();
        public int Writes { get; private set; }

        public override Task<Stream?> OpenReadAsync(VfsNodeRequest r, CancellationToken ct = default)
            => _inner.OpenReadAsync(r, ct);
        public override Task<Stream> OpenWriteAsync(VfsNodeRequest r, VfsWriteMode m = VfsWriteMode.Create, CancellationToken ct = default)
        { Writes++; return _inner.OpenWriteAsync(r, m, ct); }
        public override Task RenameAsync(VfsNodeRequest r, string newName, CancellationToken ct = default)
            => _inner.RenameAsync(r, newName, ct);
        public override Task DeleteAsync(VfsNodeRequest r, CancellationToken ct = default)
            => _inner.DeleteAsync(r, ct);
        public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest r, CancellationToken ct = default)
            => _inner.GetInfoAsync(r, ct);
        protected override IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(VfsNodeRequest r, CancellationToken ct = default)
            => _inner.ListAsync(r, VfsListOptions.Default, ct);
    }

    [Fact]
    public async Task TouchAccessed_UpdatesAccessedAt_AndPersists()
    {
        var store = new InMemoryKvNode();
        var cat   = new JsonFileVfsCatalog(store);
        await cat.PutEntryAsync(File("docs/a.txt", "h1"));

        var t = DateTimeOffset.UnixEpoch.AddDays(10);
        await cat.TouchAccessedAsync(P("docs/a.txt"), t);

        Assert.Equal(t, (await cat.GetAsync(P("docs/a.txt")))!.AccessedAt);
        // Persisted: a fresh catalog over the same store sees it.
        Assert.Equal(t, (await new JsonFileVfsCatalog(store).GetAsync(P("docs/a.txt")))!.AccessedAt);
    }

    [Fact]
    public async Task TouchAccessed_CoalescesWithinWindow_ButUpdatesBeyondIt()
    {
        var cat = new JsonFileVfsCatalog(new InMemoryKvNode());
        await cat.PutEntryAsync(File("docs/a.txt", "h1"));

        var t0 = DateTimeOffset.UnixEpoch.AddDays(10);
        await cat.TouchAccessedAsync(P("docs/a.txt"), t0);
        await cat.TouchAccessedAsync(P("docs/a.txt"), t0.AddSeconds(30));   // within the 1-min window → coalesced
        Assert.Equal(t0, (await cat.GetAsync(P("docs/a.txt")))!.AccessedAt);

        var later = t0.AddMinutes(5);
        await cat.TouchAccessedAsync(P("docs/a.txt"), later);              // beyond the window → recorded
        Assert.Equal(later, (await cat.GetAsync(P("docs/a.txt")))!.AccessedAt);
    }

    [Fact]
    public async Task TouchAccessed_NoOps_ForMissingOrDirectory()
    {
        var cat = new JsonFileVfsCatalog(new InMemoryKvNode());
        await cat.EnsureDirectoryAsync(P("docs"), DateTimeOffset.UnixEpoch);

        await cat.TouchAccessedAsync(P("missing.txt"), DateTimeOffset.UnixEpoch.AddDays(1));   // no entry
        await cat.TouchAccessedAsync(P("docs"), DateTimeOffset.UnixEpoch.AddDays(1));          // a directory

        Assert.Null(await cat.GetAsync(P("missing.txt")));
        Assert.Null((await cat.GetAsync(P("docs")))!.AccessedAt);
    }

    [Fact]
    public async Task Persists_AcrossReload_FromSameStore()
    {
        var store = new InMemoryKvNode();

        var first = new JsonFileVfsCatalog(store);
        await first.PutEntryAsync(File("reports/q3.pdf", "abc"));
        await first.PutEntryAsync(File("reports/q4.pdf", "def"));

        // A fresh catalog over the same store must load what was persisted.
        var reloaded = new JsonFileVfsCatalog(store);
        Assert.Equal("abc", (await reloaded.GetAsync(P("reports/q3.pdf")))!.ContentId);
        Assert.Equal("def", (await reloaded.GetAsync(P("reports/q4.pdf")))!.ContentId);
        Assert.True((await reloaded.GetAsync(P("reports")))!.IsDirectory);   // ancestor dir persisted too
    }

    [Fact]
    public async Task ReferenceCount_And_FindByHash()
    {
        var cat = new JsonFileVfsCatalog(new InMemoryKvNode());
        await cat.PutEntryAsync(File("a.txt", "same"));
        await cat.PutEntryAsync(File("b.txt", "same"));

        Assert.Equal(2, await cat.ReferenceCountAsync("same"));
        Assert.Equal("same", await cat.FindContentIdByHashAsync("same"));
        Assert.Null(await cat.FindContentIdByHashAsync("missing"));
    }

    [Fact]
    public async Task Remove_YieldsFiles_And_Persists()
    {
        var store = new InMemoryKvNode();
        var cat = new JsonFileVfsCatalog(store);
        await cat.PutEntryAsync(File("dir/a.txt", "h1"));
        await cat.PutEntryAsync(File("dir/b.txt", "h2"));

        var removed = new List<string>();
        await foreach (var e in cat.RemoveAsync(P("dir"))) removed.Add(e.Path.ToString());
        Assert.Equal(2, removed.Count);

        var reloaded = new JsonFileVfsCatalog(store);
        Assert.Null(await reloaded.GetAsync(P("dir/a.txt")));
    }

    [Fact]
    public async Task ForPartition_IsolatesNamespaces()
    {
        var store = new InMemoryKvNode();
        var root  = new JsonFileVfsCatalog(store);

        var a = (IPartitionedVfsCatalog)root;
        var files   = a.ForPartition("files");
        var archive = a.ForPartition("archive");

        await files.PutEntryAsync(File("x.txt", "hx"));
        await archive.PutEntryAsync(File("x.txt", "hy"));   // same path, different partition

        Assert.Equal("hx", (await files.GetAsync(P("x.txt")))!.ContentId);
        Assert.Equal("hy", (await archive.GetAsync(P("x.txt")))!.ContentId);   // no collision
    }

    [Fact]
    public async Task Persists_NewFields_AcrossReload()
    {
        var store = new InMemoryKvNode();
        var cat   = new JsonFileVfsCatalog(store);

        var props = new Dictionary<string, string?>();
        props.Put("etag", "abc123");
        props.Put("partCount", 7);
        props.PutJson("checksums", new[] { "sha256:aa", "crc32:bb" });   // structured → JSON string

        await cat.PutEntryAsync(File("docs/a.txt", "h1") with
        {
            IsHidden   = true,
            AccessedAt = DateTimeOffset.UnixEpoch.AddHours(3),
            ContentType = "text/plain",
            Properties = props,
        });

        var reloaded = new JsonFileVfsCatalog(store);   // fresh index, same backing store
        var e = await reloaded.GetAsync(P("docs/a.txt"));

        Assert.NotNull(e);
        Assert.True(e!.IsHidden);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(3), e.AccessedAt);
        Assert.Equal("text/plain", e.ContentType);
        Assert.Equal("abc123", e.Properties.GetString("etag"));
        Assert.Equal(7, e.Properties.GetInt("partCount"));
        Assert.Equal(new[] { "sha256:aa", "crc32:bb" }, e.Properties.GetJson<string[]>("checksums"));
    }

    [Fact]
    public async Task Loads_LegacyDocument_WithoutNewFields()
    {
        // A catalog document written before IsHidden/AccessedAt/Properties existed: those keys
        // are simply absent. It must still load, with the new fields defaulted.
        var store = new InMemoryKvNode();
        const string legacy = """[{"Path":"docs/a.txt","IsDirectory":false,"ContentId":"h1","Hash":"h1","Size":5,"CreatedAt":"1970-01-01T00:00:00+00:00","ModifiedAt":"1970-01-01T00:00:00+00:00"}]""";
        await using (var w = await store.OpenWriteAsync(new VfsNodeRequest(P(".vfs-catalog.json"))))
            await w.WriteAsync(System.Text.Encoding.UTF8.GetBytes(legacy));

        var cat = new JsonFileVfsCatalog(store);
        var e   = await cat.GetAsync(P("docs/a.txt"));

        Assert.NotNull(e);
        Assert.Equal("h1", e!.ContentId);
        Assert.False(e.IsHidden);
        Assert.Null(e.AccessedAt);
        Assert.Null(e.Properties);
        Assert.Null(e.Properties.GetString("anything"));   // accessor tolerates a null bag
    }
}
