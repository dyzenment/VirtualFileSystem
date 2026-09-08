using Dytools.VirtualFileSystem.Nodes.InMemory;
using Dytools.VirtualFileSystem.Nodes.LocalFs;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class LocalPathMappingTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "vfs-map-" + Guid.NewGuid().ToString("N"));

    private string Dir(params string[] parts)
    {
        var p = Path.Combine([_base, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    // -- Host path -> VFS path -------------------------------------------------

    [Fact]
    public void TryGetVfsPath_MapsAPathUnderAMount()
    {
        var root = Dir("data");
        var vfs  = VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(root)));

        Assert.True(vfs.TryGetVfsPath(Path.Combine(root, "docs", "report.pdf"), out var vfsPath));
        Assert.Equal("/local/docs/report.pdf", vfsPath);
    }

    [Fact]
    public void TryGetVfsPath_TheMountRootItself()
    {
        var root = Dir("data");
        var vfs  = VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(root)));

        Assert.True(vfs.TryGetVfsPath(root, out var vfsPath));
        Assert.Equal("/local", vfsPath);
    }

    [Fact]
    public void TryGetVfsPath_MostSpecificMountWins()
    {
        var outer = Dir("data");
        var inner = Dir("data", "projects");
        var vfs   = VfsFactory.Build(b => b
            .Mount("/local",    new LocalFsNode(outer))
            .Mount("/projects", new LocalFsNode(inner)));

        Assert.True(vfs.TryGetVfsPath(Path.Combine(inner, "app", "main.cs"), out var vfsPath));
        Assert.Equal("/projects/app/main.cs", vfsPath);

        // Both routes are real, and both are offered - ranked, not hidden.
        var all = vfs.GetVfsPathCandidates(Path.Combine(inner, "app", "main.cs"));
        Assert.Equal(2, all.Count);
        Assert.Equal("/projects/app/main.cs", all[0].Path);
        Assert.Equal("/local/projects/app/main.cs", all[1].Path);
    }

    [Fact]
    public void TryGetVfsPath_UncoveredPath_ReturnsFalse()
    {
        var vfs = VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(Dir("data"))));

        Assert.False(vfs.TryGetVfsPath(Path.Combine(_base, "elsewhere", "x.txt"), out var vfsPath));
        Assert.Equal(string.Empty, vfsPath);
    }

    [Fact]
    public void TryGetVfsPath_NodeWithNoHostStorage_ReturnsFalse()
    {
        var vfs = VfsFactory.Build(b => b.Mount("/mem", new InMemoryKvNode()));
        Assert.False(vfs.TryGetVfsPath(Path.Combine(_base, "x.txt"), out _));
    }

    [Fact]
    public void TryGetVfsPath_SiblingSharingATextualPrefix_IsNotInside()
    {
        // Root "…/app" must not swallow "…/apple" - a prefix match without a segment boundary would.
        var root    = Dir("app");
        var sibling = Dir("apple");
        var vfs     = VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(root)));

        Assert.False(vfs.TryGetVfsPath(Path.Combine(sibling, "secret.txt"), out _));
    }

    [Fact]
    public void TryGetVfsPath_NormalisesTheIncomingPath()
    {
        var root = Dir("data");
        var vfs  = VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(root)));

        // Host paths arrive from pickers and command lines, not through VfsPath - so "." and ".."
        // have to be resolved here rather than assumed gone.
        var messy = Path.Combine(root, "docs", "..", ".", "docs", "report.pdf");
        Assert.True(vfs.TryGetVfsPath(messy, out var vfsPath));
        Assert.Equal("/local/docs/report.pdf", vfsPath);
    }

    // -- VFS path -> host path -------------------------------------------------
    //
    // For an entry that exists this is GetInfoAsync().LocalPath - there is no second way to ask.
    // The capability covers the one thing that cannot: where a file would go before it is written.

    [Fact]
    public async Task HostPathFromGetInfo_RoundTripsBackToTheVfsPath()
    {
        var root = Dir("data");
        var vfs  = VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(root)));
        await vfs.WriteStringAsync("/local/docs/report.pdf", "payload");

        var info = await vfs.GetInfoAsync("/local/docs/report.pdf");
        Assert.NotNull(info);
        Assert.Equal(Path.Combine(root, "docs", "report.pdf"), info.LocalPath);

        Assert.True(vfs.TryGetVfsPath(info.LocalPath!, out var back));
        Assert.Equal("/local/docs/report.pdf", back);
    }

    [Fact]
    public void Capability_AnswersForAnEntryThatDoesNotExistYet()
    {
        var root = Dir("data");
        var vfs  = VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(root)));

        var mapping = vfs.GetNodeCapability<ILocalPathMapping>("/local");
        Assert.NotNull(mapping);

        var host = mapping.ToLocalPath(VfsPath.From("not/created/yet.txt"));
        Assert.Equal(Path.Combine(root, "not", "created", "yet.txt"), host);
        Assert.False(File.Exists(host));
    }

    [Fact]
    public void Capability_IsAbsentOnANodeWithNoHostStorage()
    {
        var vfs = VfsFactory.Build(b => b.Mount("/mem", new InMemoryKvNode()));
        Assert.Null(vfs.GetNodeCapability<ILocalPathMapping>("/mem"));
    }

    // -- Aliases and internal mounts -------------------------------------------

    [Fact]
    public void Candidates_OfferAliasRoutesAfterTheDirectPath()
    {
        var root = Dir("data");
        var vfs  = VfsFactory.Build(b => b
            .Mount("/local", new LocalFsNode(root))
            .Alias("/shortcut", "/local/docs"));

        var candidates = vfs.GetVfsPathCandidates(Path.Combine(root, "docs", "report.pdf"));

        Assert.Equal(2, candidates.Count);
        Assert.Equal("/local/docs/report.pdf", candidates[0].Path);
        Assert.Null(candidates[0].ViaAlias);
        Assert.Equal("/shortcut/report.pdf", candidates[1].Path);
        Assert.Equal("/shortcut", candidates[1].ViaAlias);
    }

    [Fact]
    public void Candidates_ExcludeAliases_WhenAsked()
    {
        var root = Dir("data");
        var vfs  = VfsFactory.Build(b => b
            .Mount("/local", new LocalFsNode(root))
            .Alias("/shortcut", "/local/docs"));

        var candidates = vfs.GetVfsPathCandidates(
            Path.Combine(root, "docs", "report.pdf"), new VfsPathLookupOptions { IncludeAliases = false });

        Assert.Single(candidates);
        Assert.Equal("/local/docs/report.pdf", candidates[0].Path);
    }

    [Fact]
    public async Task InternalMount_OnlyTheAliasRouteIsOffered_AndItWorks()
    {
        var root = Dir("data");
        var vfs  = VfsFactory.Build(b => b
            .Mount("/dev/local", new LocalFsNode(root), isInternal: true)
            .Alias("/files", "/dev/local"));

        var candidates = vfs.GetVfsPathCandidates(Path.Combine(root, "report.pdf"));

        // The direct path would throw on use, so it is not offered - only the door that opens.
        Assert.Single(candidates);
        Assert.Equal("/files/report.pdf", candidates[0].Path);
        Assert.Equal("/files", candidates[0].ViaAlias);
        Assert.True(candidates[0].IsInternal);

        Assert.True(vfs.TryGetVfsPath(Path.Combine(root, "report.pdf"), out var usable));
        await vfs.WriteStringAsync(usable, "payload");
        Assert.Equal("payload", await vfs.ReadAsStringAsync(usable));
    }

    [Fact]
    public void InternalMount_WithNoAlias_IsNotReachable()
    {
        var root = Dir("data");
        var vfs  = VfsFactory.Build(b => b.Mount("/dev/local", new LocalFsNode(root), isInternal: true));

        Assert.False(vfs.TryGetVfsPath(Path.Combine(root, "report.pdf"), out _));

        var withInternal = vfs.GetVfsPathCandidates(
            Path.Combine(root, "report.pdf"), new VfsPathLookupOptions { IncludeInternal = true });
        Assert.Single(withInternal);
        Assert.True(withInternal[0].IsInternal);
        Assert.Equal("/dev/local/report.pdf", withInternal[0].Path);
    }

    [Fact]
    public void InstanceMounts_AreVisibleAlongsideBuilderMounts()
    {
        var builderRoot  = Dir("builder");
        var instanceRoot = Dir("instance");
        var vfs = VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(builderRoot)));
        vfs.Mount("/scratch", new LocalFsNode(instanceRoot));

        Assert.True(vfs.TryGetVfsPath(Path.Combine(instanceRoot, "a.txt"), out var fromInstance));
        Assert.Equal("/scratch/a.txt", fromInstance);

        Assert.True(vfs.TryGetVfsPath(Path.Combine(builderRoot, "b.txt"), out var fromBuilder));
        Assert.Equal("/local/b.txt", fromBuilder);
    }
}
