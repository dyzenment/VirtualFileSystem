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
}
