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
        await cat.PutFileAsync(File("docs/a.txt", "h1"));
        await cat.PutFileAsync(File("docs/b.txt", "h2"));

        Assert.Equal("h1", (await cat.GetAsync(P("docs/a.txt")))!.ContentId);

        var names = new List<string>();
        await foreach (var e in cat.ListChildrenAsync(P("docs"))) names.Add(e.Path.ToString());
        Assert.Contains("docs/a.txt", names);
        Assert.Contains("docs/b.txt", names);
    }

    [Fact]
    public async Task Persists_AcrossReload_FromSameStore()
    {
        var store = new InMemoryKvNode();

        var first = new JsonFileVfsCatalog(store);
        await first.PutFileAsync(File("reports/q3.pdf", "abc"));
        await first.PutFileAsync(File("reports/q4.pdf", "def"));

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
        await cat.PutFileAsync(File("a.txt", "same"));
        await cat.PutFileAsync(File("b.txt", "same"));

        Assert.Equal(2, await cat.ReferenceCountAsync("same"));
        Assert.Equal("same", await cat.FindContentIdByHashAsync("same"));
        Assert.Null(await cat.FindContentIdByHashAsync("missing"));
    }

    [Fact]
    public async Task Remove_YieldsFiles_And_Persists()
    {
        var store = new InMemoryKvNode();
        var cat = new JsonFileVfsCatalog(store);
        await cat.PutFileAsync(File("dir/a.txt", "h1"));
        await cat.PutFileAsync(File("dir/b.txt", "h2"));

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

        await files.PutFileAsync(File("x.txt", "hx"));
        await archive.PutFileAsync(File("x.txt", "hy"));   // same path, different partition

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

        await cat.PutFileAsync(File("docs/a.txt", "h1") with
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
