using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Tests;

// The listing fast path skips per-entry resolution when nothing under the directory can route a
// child elsewhere. These pin down both branches: that the common case is unchanged, and that a
// genuine shadow is still detected.
public sealed class ShadowingListTests
{
    private static async Task SeedAsync(IVirtualFileSystem vfs, params string[] paths)
    {
        foreach (var p in paths) await vfs.WriteStringAsync(p, "x");
    }

    [Fact]
    public async Task NoShadowing_EntriesCarryTheDirectorysOwnMount()
    {
        var vfs = VfsFactory.CreateDual();
        await SeedAsync(vfs, "/a/one.txt", "/a/two.txt");

        var entries = new List<VfsEntryInfo>();
        await foreach (var e in vfs.ListInfoAsync("/a")) entries.Add(e);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.StartsWith("/a/", e.Path));
        Assert.All(entries, e => Assert.False(e.IsAliased));
    }

    [Fact]
    public async Task AliasedDirectory_ChildrenReportTheResolvedMount()
    {
        // Listing through an alias: children come back under the mount the alias resolved to.
        var vfs = VfsFactory.Build(b => b
            .Mount("/real", new InMemoryKvNode())
            .Alias("/link", "/real"));

        await SeedAsync(vfs, "/link/file.txt");

        var entries = new List<VfsEntryInfo>();
        await foreach (var e in vfs.ListInfoAsync("/link")) entries.Add(e);

        var only = Assert.Single(entries);
        Assert.Equal("file.txt", only.Name);
        Assert.Equal("/real/file.txt", only.Path);
    }

    [Fact]
    public async Task AliasUnderTheListedDirectory_IsDetectedAsShadowing()
    {
        // An alias key below the listed path can route one child elsewhere, so the listing must not
        // take the fast path here.
        var vfs = VfsFactory.Build(b => b
            .Mount("/a", new InMemoryKvNode())
            .Mount("/elsewhere", new InMemoryKvNode())
            .Alias("/a/shadowed.txt", "/elsewhere/real.txt"));

        await SeedAsync(vfs, "/a/plain.txt");

        var entries = new List<VfsEntryInfo>();
        await foreach (var e in vfs.ListInfoAsync("/a")) entries.Add(e);

        // The alias materialises nothing, so only the real entry is listed.
        var only = Assert.Single(entries);
        Assert.Equal("plain.txt", only.Name);
        Assert.Equal("/a/plain.txt", only.Path);
    }

    [Fact]
    public async Task NestedMount_ShadowedChildStillListsWithACoherentPath()
    {
        var outer = new InMemoryKvNode();
        var inner = new InMemoryKvNode();
        var vfs = VfsFactory.Build(b => b.Mount("/a", outer).Mount("/a/sub", inner));

        // Written through /a's own node, so /a reports "sub" as a child directory.
        await SeedAsync(vfs, "/a/top.txt");

        var entries = new List<VfsEntryInfo>();
        await foreach (var e in vfs.ListInfoAsync("/a")) entries.Add(e);

        foreach (var e in entries)
            Assert.True(e.Path.StartsWith("/a/", StringComparison.Ordinal),
                        $"entry path '{e.Path}' should stay under the listed directory");
    }
}
