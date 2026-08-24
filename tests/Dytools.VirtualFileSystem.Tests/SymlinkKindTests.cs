namespace Dytools.VirtualFileSystem.Tests;

// A symlink is a kind the node reports, so a listing can say which entries are links - the gap that
// made ListInfoAsync and GetInfoAsync disagree about the same file.
public sealed class SymlinkKindTests
{
    private static IVirtualFileSystem Build()
    {
        var node = new LinkTestNode();
        node.Store("real.dat", "content");
        node.Link("good.lnk", "/s/real.dat");
        node.Link("broken.lnk", "/s/gone.dat");
        return VfsFactory.Build(b => b.Mount("/s", node).UseSymlinks());
    }

    private static async Task<Dictionary<string, VfsEntryInfo>> ListAsync(
        IVirtualFileSystem vfs, VfsListOptions? options = null)
    {
        var byName = new Dictionary<string, VfsEntryInfo>(StringComparer.OrdinalIgnoreCase);
        await foreach (var e in vfs.ListInfoAsync("/s", options ?? VfsListOptions.Default))
            byName[e.Name] = e;
        return byName;
    }

    [Fact]
    public async Task Listing_MarksSymlinks()
    {
        var entries = await ListAsync(Build(), new VfsListOptions { Kind = VfsEntryKind.All });

        Assert.True(entries["good.lnk"].IsSymlink);
        Assert.True(entries["broken.lnk"].IsSymlink);
        Assert.False(entries["real.dat"].IsSymlink);
    }

    [Fact]
    public async Task Listing_ReportsTheTarget()
    {
        var entries = await ListAsync(Build(), new VfsListOptions { Kind = VfsEntryKind.All });

        Assert.Equal("/s/real.dat", entries["good.lnk"].SymlinkTarget);
        Assert.Null(entries["real.dat"].SymlinkTarget);
    }

    [Fact]
    public async Task Listing_NeverFollows()
    {
        // Listings act on the pointer, so the traversal flag stays false and names stay the link's.
        var entries = await ListAsync(Build(), new VfsListOptions { Kind = VfsEntryKind.All });

        Assert.All(entries.Values, e => Assert.False(e.FollowedSymlink));
        Assert.Contains("good.lnk", entries.Keys);
    }

    [Fact]
    public async Task Listing_AgreesWithNoFollowGetInfo()
    {
        // The disagreement that started this: both must describe the same object.
        var vfs     = Build();
        var entries = await ListAsync(vfs, new VfsListOptions { Kind = VfsEntryKind.All });
        var info    = await vfs.GetInfoAsync("/s/good.lnk", VfsMetadataOptions.NoFollow);

        Assert.Equal(info!.IsSymlink, entries["good.lnk"].IsSymlink);
        Assert.Equal(info.SymlinkTarget, entries["good.lnk"].SymlinkTarget);
        Assert.Equal(info.Name, entries["good.lnk"].Name);
    }

    // -- Kind filtering --------------------------------------------------------

    [Fact]
    public async Task Kind_Symlinks_ReturnsOnlyLinks()
    {
        var entries = await ListAsync(Build(), new VfsListOptions { Kind = VfsEntryKind.Symlinks });

        Assert.Equal(2, entries.Count);
        Assert.All(entries.Values, e => Assert.True(e.IsSymlink));
    }

    [Fact]
    public async Task Kind_Files_ExcludesLinks()
    {
        // A link is matched on being a link first, so asking for files must not sweep them in.
        var entries = await ListAsync(Build(), new VfsListOptions { Kind = VfsEntryKind.Files });

        var only = Assert.Single(entries);
        Assert.Equal("real.dat", only.Key);
    }

    [Fact]
    public async Task Kind_Default_ExcludesLinks()
    {
        // Both = Files | Directories, so the default listing leaves pointers out.
        Assert.DoesNotContain("good.lnk", (await ListAsync(Build())).Keys);
    }

    [Fact]
    public async Task Kind_All_IncludesEverything()
        => Assert.Equal(3, (await ListAsync(Build(), new VfsListOptions { Kind = VfsEntryKind.All })).Count);
}

// Symlink-ness has to survive a catalog round-trip, or a mirrored listing loses it - the same bug
// re-entering through the cache. It rides in Properties, so no catalog schema change is needed.
public sealed class SymlinkCatalogRoundTripTests
{
    [Fact]
    public async Task CatalogRoundTrip_KeepsKindAndTarget()
    {
        var catalog = new InMemoryVfsCatalog();
        var mirror  = new Catalog.NodeCatalog(catalog);

        await mirror.UpsertAsync(new VfsNodeInfo
        {
            RelativePath  = VfsPath.From("docs/link.lnk"),
            IsFile        = true,
            IsDirectory   = false,
            IsSymlink     = true,
            SymlinkTarget = "/s/real.dat",
        });

        var round = Catalog.NodeCatalog.ToNodeInfo(
            (await mirror.GetAsync(VfsPath.From("docs/link.lnk")))!);

        Assert.True(round.IsSymlink);
        Assert.Equal("/s/real.dat", round.SymlinkTarget);
    }

    [Fact]
    public async Task CatalogRoundTrip_LeavesOrdinaryEntriesAlone()
    {
        var catalog = new InMemoryVfsCatalog();
        var mirror  = new Catalog.NodeCatalog(catalog);

        await mirror.UpsertAsync(new VfsNodeInfo
        {
            RelativePath = VfsPath.From("docs/plain.txt"), IsFile = true, IsDirectory = false,
        });

        var round = Catalog.NodeCatalog.ToNodeInfo(
            (await mirror.GetAsync(VfsPath.From("docs/plain.txt")))!);

        Assert.False(round.IsSymlink);
        Assert.Null(round.SymlinkTarget);
    }
}
