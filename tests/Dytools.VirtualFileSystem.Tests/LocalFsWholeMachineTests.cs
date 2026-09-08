using Dytools.VirtualFileSystem.Nodes.LocalFs;

namespace Dytools.VirtualFileSystem.Tests;

/// <summary>
/// A LocalFsNode rooted at the whole machine. On this platform (and every one but Windows) that is
/// a node rooted at "/", so these exercise the real end-to-end behaviour; the Windows volume tier's
/// conventions are covered by <see cref="LocalFsVolumeTests"/>, which needs no drive letters.
/// </summary>
public sealed class LocalFsWholeMachineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vfs-whole-" + Guid.NewGuid().ToString("N"));

    private IVirtualFileSystem Create(string root = "")
    {
        Directory.CreateDirectory(_dir);
        return VfsFactory.Build(b => b.Mount("/host", new LocalFsNode(root)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public void BothSpellings_MeanTheWholeMachine(string root)
        => Assert.True(new LocalFsNode(root).IsWholeMachineRoot);

    [Fact]
    public void ADirectoryRoot_IsNotAWholeMachineRoot()
        => Assert.False(new LocalFsNode(Path.GetTempPath()).IsWholeMachineRoot);

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public async Task ReadsAndWritesAnywhereOnTheMachine(string root)
    {
        var vfs  = Create(root);
        var file = Path.Combine(_dir, "note.txt");
        await File.WriteAllTextAsync(file, "from outside the vfs");

        Assert.True(vfs.TryGetVfsPath(file, out var vfsPath));
        Assert.Equal("from outside the vfs", await vfs.ReadAsStringAsync(vfsPath));

        await vfs.WriteStringAsync(vfsPath, "written through the vfs");
        Assert.Equal("written through the vfs", await File.ReadAllTextAsync(file));
    }

    // The case this exists for: a picked file that is already inside a mount must resolve to its own
    // VFS path. Without that the only way to "import" it is to copy it over itself.
    [Fact]
    public async Task APickedFile_ResolvesToItsOwnPath_RatherThanNeedingACopy()
    {
        var vfs  = Create();
        var file = Path.Combine(_dir, "picked.txt");
        await File.WriteAllTextAsync(file, "payload");

        Assert.True(vfs.TryGetVfsPath(file, out var vfsPath));

        var info = await vfs.GetInfoAsync(vfsPath);
        Assert.NotNull(info);
        Assert.Equal(file, info.LocalPath);          // same file, not a copy of it
    }

    [Fact]
    public async Task ACuratedMountStillWins_WhenBothCover()
    {
        Directory.CreateDirectory(_dir);
        var vfs = VfsFactory.Build(b => b
            .Mount("/host",     new LocalFsNode(""))
            .Mount("/projects", new LocalFsNode(_dir)));

        var file = Path.Combine(_dir, "app", "main.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "x");

        // The whole-machine mount is a fallback, not a takeover: the specific mount ranks first.
        Assert.True(vfs.TryGetVfsPath(file, out var best));
        Assert.Equal("/projects/app/main.cs", best);

        var all = vfs.GetVfsPathCandidates(file);
        Assert.Equal(2, all.Count);
        Assert.Equal("/projects/app/main.cs", all[0].Path);
        Assert.StartsWith("/host/", all[1].Path);
    }

    [Fact]
    public async Task Listing_WorksAnywhere()
    {
        var vfs = Create();
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_dir, "b.txt"), "b");

        Assert.True(vfs.TryGetVfsPath(_dir, out var vfsDir));

        var names = new List<string>();
        await foreach (var e in vfs.ListInfoAsync(vfsDir)) names.Add(e.Name);

        Assert.Contains("a.txt", names);
        Assert.Contains("b.txt", names);
    }

    [Fact]
    public async Task TheMountRoot_IsADirectory()
    {
        var vfs = Create();
        var info = await vfs.GetInfoAsync("/host");

        Assert.NotNull(info);
        Assert.True(info.IsDirectory);
        Assert.True(await vfs.ExistsAsync("/host"));
    }

    [Fact]
    public async Task ListingTheMountRoot_Enumerates()
    {
        // On Unix this lists "/" as an ordinary directory; on Windows it lists the volumes. Either
        // way it answers instead of throwing, which is all a root listing has to do.
        var vfs = Create();

        var count = 0;
        await foreach (var _ in vfs.ListInfoAsync("/host")) count++;

        Assert.True(count > 0);
    }

    [Fact]
    public void RoundTripsThroughTheCapability()
    {
        var vfs = Create();
        var mapping = vfs.GetNodeCapability<ILocalPathMapping>("/host");
        Assert.NotNull(mapping);

        var file = Path.Combine(_dir, "not-created-yet.txt");
        Assert.True(vfs.TryGetVfsPath(file, out var vfsPath));

        var relative = vfsPath["/host".Length..].TrimStart('/');
        Assert.Equal(file, mapping.ToLocalPath(VfsPath.From(relative)));
    }

    [Fact]
    public void ExtendedLengthPrefix_IsStrippedOnTheWayIn()
    {
        // A picker hands these back for long paths; nothing downstream understands them.
        var vfs = Create(Path.GetTempPath());
        var normal   = Path.Combine(Path.GetTempPath(), "x.txt");
        var prefixed = @"\\?\" + normal;

        Assert.True(vfs.TryGetVfsPath(normal, out var a));
        Assert.True(vfs.TryGetVfsPath(prefixed, out var b));
        Assert.Equal(a, b);
    }
}
