namespace Dytools.VirtualFileSystem.Tests;

public sealed class GetInfoListTests
{
    // -- GetInfo ---------------------------------------------------------------

    [Fact]
    public async Task GetInfo_ExistingFile_ReturnsEntry()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "content");
        var info = await vfs.GetInfoAsync("/a/file.txt");
        Assert.NotNull(info);
        Assert.True(info.IsFile);
        Assert.False(info.IsDirectory);
    }

    [Fact]
    public async Task GetInfo_ExistingFile_PathMatchesRequest()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "data");
        var info = await vfs.GetInfoAsync("/a/file.txt");
        Assert.NotNull(info);
        Assert.Equal("/a/file.txt", info.Path);
    }

    [Fact]
    public async Task GetInfo_ExistingFile_SizeBytesIsCorrect()
    {
        var vfs   = VfsFactory.CreateDual();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        await using (var s = await vfs.OpenWriteAsync("/a/bin.dat"))
            await s.WriteAsync(bytes);

        var info = await vfs.GetInfoAsync("/a/bin.dat");
        Assert.Equal(5L, info?.SizeBytes);
    }

    [Fact]
    public async Task GetInfo_MissingFile_ReturnsNull()
    {
        var vfs = VfsFactory.CreateDual();
        var info = await vfs.GetInfoAsync("/a/ghost.txt");
        Assert.Null(info);
    }

    [Fact]
    public async Task GetInfo_IsAliased_FalseForDirectAccess()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "x");
        var info = await vfs.GetInfoAsync("/a/file.txt");
        Assert.False(info?.IsAliased);
    }

    [Fact]
    public async Task GetInfo_IsSymlink_FalseWithNoMiddleware()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "x");
        var info = await vfs.GetInfoAsync("/a/file.txt");
        Assert.False(info?.IsSymlink);
    }

    // -- List ------------------------------------------------------------------

    [Fact]
    public async Task List_ReturnsDirectChildNames()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/dir/alpha.txt", "a");
        await VfsFactory.WriteTextAsync(vfs, "/a/dir/beta.txt",  "b");

        var paths = new List<string>();
        await foreach (var path in vfs.ListAsync("/a/dir"))
            paths.Add(path);

        Assert.Contains("/a/dir/alpha.txt", paths);
        Assert.Contains("/a/dir/beta.txt",  paths);
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public async Task List_ReturnsImmediateChildrenOnly()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/dir/sub/nested.txt", "deep");
        await VfsFactory.WriteTextAsync(vfs, "/a/dir/direct.txt",     "shallow");

        var paths = new List<string>();
        await foreach (var path in vfs.ListAsync("/a/dir"))
            paths.Add(path);

        // Immediate children: the direct file and the subdirectory - but not the deeper file.
        Assert.Contains("/a/dir/direct.txt", paths);
        Assert.Contains("/a/dir/sub",        paths);
        Assert.DoesNotContain("/a/dir/sub/nested.txt", paths);
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public async Task List_Recursive_ReturnsWholeSubtree()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/dir/sub/nested.txt", "deep");
        await VfsFactory.WriteTextAsync(vfs, "/a/dir/direct.txt",     "shallow");

        var paths = new List<string>();
        await foreach (var path in vfs.ListAsync("/a/dir", new VfsListOptions { Recurse = true }))
            paths.Add(path);

        Assert.Contains("/a/dir/direct.txt",     paths);
        Assert.Contains("/a/dir/sub",            paths);
        Assert.Contains("/a/dir/sub/nested.txt", paths);
    }

    [Fact]
    public async Task List_SearchPattern_FiltersByGlob()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/docs/report.pdf", "a");
        await VfsFactory.WriteTextAsync(vfs, "/a/docs/notes.txt",  "b");
        await VfsFactory.WriteTextAsync(vfs, "/a/docs/summary.pdf", "c");

        var paths = new List<string>();
        await foreach (var path in vfs.ListAsync("/a/docs", new VfsListOptions { SearchPattern = "*.pdf" }))
            paths.Add(path);

        Assert.Contains("/a/docs/report.pdf",  paths);
        Assert.Contains("/a/docs/summary.pdf", paths);
        Assert.DoesNotContain("/a/docs/notes.txt", paths);
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public async Task List_EmptyDirectory_ReturnsNothing()
    {
        var vfs = VfsFactory.CreateDual();
        var names = new List<string>();
        await foreach (var name in vfs.ListAsync("/a/empty"))
            names.Add(name);
        Assert.Empty(names);
    }

    [Fact]
    public async Task ListInfo_ReturnsSizeAndPath()
    {
        var vfs   = VfsFactory.CreateDual();
        var bytes = new byte[42];
        await using (var s = await vfs.OpenWriteAsync("/a/dir/sized.dat"))
            await s.WriteAsync(bytes);

        var entries = new List<VfsEntryInfo>();
        await foreach (var e in vfs.ListInfoAsync("/a/dir"))
            entries.Add(e);

        var entry = Assert.Single(entries);
        Assert.Equal(42L, entry.SizeBytes);
        Assert.EndsWith("sized.dat", entry.Path);
    }

    [Fact]
    public async Task ListInfo_IsFile_TrueForFiles()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/dir/file.txt", "x");

        var entries = new List<VfsEntryInfo>();
        await foreach (var e in vfs.ListInfoAsync("/a/dir"))
            entries.Add(e);

        Assert.All(entries, e => Assert.True(e.IsFile));
    }

    // -- GetCapability ---------------------------------------------------------

    [Fact]
    public void GetCapability_UnknownInterface_ReturnsNull()
    {
        var vfs = VfsFactory.CreateDual();
        // InMemoryKvNode does not implement IDeduplicatingNode
        var cap = vfs.GetCapability<IDeduplicatingNode>("/a/file.txt");
        Assert.Null(cap);
    }
}
