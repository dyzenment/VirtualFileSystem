using Dytools.VirtualFileSystem.Catalog;
using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Tests;

// The shared catalog-mirror plumbing used by the S3, Azure, and SharePoint caching nodes.
public sealed class CatalogMirrorTests
{
    private static CatalogMirror NewMirror() => new(new JsonFileVfsCatalog(new InMemoryKvNode()));

    private static VfsNodeInfo File(string path, long size = 1) => new()
    {
        RelativePath = VfsPath.From(path),
        IsFile       = true,
        IsDirectory  = false,
        SizeBytes    = size,
        ModifiedAt   = DateTimeOffset.UnixEpoch,
    };

    private static async Task<List<string>> Children(CatalogMirror m, string path)
    {
        var list = new List<string>();
        await foreach (var e in m.ListChildrenAsync(VfsPath.From(path))) list.Add(e.Path.ToString());
        return list;
    }

    [Fact]
    public async Task Upsert_Then_Serve_ImmediateChildren()
    {
        var m = NewMirror();
        await m.UpsertAsync(File("docs/a.txt"));
        await m.UpsertAsync(File("docs/b.txt"));

        var kids = await Children(m, "docs");
        Assert.Contains("docs/a.txt", kids);
        Assert.Contains("docs/b.txt", kids);
        Assert.Equal(2, kids.Count);
    }

    [Fact]
    public async Task Remove_Deletes_From_Mirror()
    {
        var m = NewMirror();
        await m.UpsertAsync(File("docs/a.txt"));
        await m.RemoveAsync(VfsPath.From("docs/a.txt"));

        Assert.Empty(await Children(m, "docs"));
    }

    [Fact]
    public async Task Move_ReKeys_Only_When_Present()
    {
        var m = NewMirror();
        await m.UpsertAsync(File("a.txt"));

        await m.MoveAsync(VfsPath.From("a.txt"), VfsPath.From("b.txt"));
        var root = await Children(m, "");
        Assert.Contains("b.txt", root);
        Assert.DoesNotContain("a.txt", root);

        // Moving something absent is a no-op (doesn't throw).
        await m.MoveAsync(VfsPath.From("ghost.txt"), VfsPath.From("x.txt"));
        Assert.DoesNotContain("x.txt", await Children(m, ""));
    }

    [Fact]
    public async Task State_RoundTrips_And_Is_Hidden_From_Listings()
    {
        var m = NewMirror();
        await m.UpsertAsync(File("a.txt"));
        await m.SetStateAsync("seeded", "1");
        await m.SetStateAsync("cursor", "abc");

        Assert.Equal("1",   await m.GetStateAsync("seeded"));
        Assert.Equal("abc", await m.GetStateAsync("cursor"));   // second key preserved

        // The reserved state entry never shows up in a listing.
        var root = await Children(m, "");
        Assert.Contains("a.txt", root);
        Assert.DoesNotContain(".vfs-mirror-state", root);
        Assert.Single(root);
    }

    [Fact]
    public async Task Clear_Wipes_Entries_But_Keeps_State()
    {
        var m = NewMirror();
        await m.UpsertAsync(File("docs/a.txt"));
        await m.UpsertAsync(File("b.txt"));
        await m.SetStateAsync("seeded", "1");

        await m.ClearAsync();

        Assert.Empty(await Children(m, ""));
        Assert.Equal("1", await m.GetStateAsync("seeded"));   // survives the wipe
    }

    [Fact]
    public void ToNodeInfo_Maps_Fields()
    {
        var entry = new CatalogEntry
        {
            Path = VfsPath.From("docs/a.txt"), IsDirectory = false, Size = 42,
            ModifiedAt = DateTimeOffset.UnixEpoch, ContentType = "text/plain",
            Properties = new Dictionary<string, string?> { ["ETag"] = "e1" },
        };

        var info = CatalogMirror.ToNodeInfo(entry);
        Assert.True(info.IsFile);
        Assert.Equal(42, info.SizeBytes);
        Assert.Equal("e1", info.Properties.GetString("ETag"));
    }
}
