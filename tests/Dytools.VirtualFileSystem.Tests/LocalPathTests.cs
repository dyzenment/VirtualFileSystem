using Dytools.VirtualFileSystem.Nodes.InMemory;
using Dytools.VirtualFileSystem.Nodes.LocalFs;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class LocalPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfs-localpath-" + Guid.NewGuid().ToString("N"));

    private IVirtualFileSystem CreateLocal()
    {
        Directory.CreateDirectory(_root);
        return VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(_root)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task GetInfo_ReportsTheHostPath_ForAFile()
    {
        var vfs = CreateLocal();
        await vfs.WriteStringAsync("/local/docs/report.txt", "payload");

        var info = await vfs.GetInfoAsync("/local/docs/report.txt");

        Assert.NotNull(info);
        Assert.Equal(Path.Combine(_root, "docs", "report.txt"), info.LocalPath);
        Assert.True(File.Exists(info.LocalPath));
    }

    [Fact]
    public async Task GetInfo_ReportsTheHostPath_ForADirectory()
    {
        var vfs = CreateLocal();
        await vfs.WriteStringAsync("/local/docs/report.txt", "payload");

        var info = await vfs.GetInfoAsync("/local/docs");

        Assert.NotNull(info);
        Assert.Equal(Path.Combine(_root, "docs"), info.LocalPath);
        Assert.True(Directory.Exists(info.LocalPath));
    }

    [Fact]
    public async Task Listing_ReportsTheHostPath_ForEveryEntry()
    {
        var vfs = CreateLocal();
        await vfs.WriteStringAsync("/local/a.txt", "a");
        await vfs.WriteStringAsync("/local/sub/b.txt", "b");

        var entries = new List<VfsEntryInfo>();
        await foreach (var e in vfs.ListInfoAsync("/local", new VfsListOptions { Recurse = true }))
            entries.Add(e);

        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            Assert.NotNull(e.LocalPath);
            Assert.StartsWith(_root, e.LocalPath);
            Assert.True(File.Exists(e.LocalPath) || Directory.Exists(e.LocalPath));
        });
    }

    [Fact]
    public async Task LocalPath_SurvivesAnAlias()
    {
        Directory.CreateDirectory(_root);
        var vfs = VfsFactory.Build(b => b
            .Mount("/local", new LocalFsNode(_root))
            .Alias("/shortcut", "/local/docs"));
        await vfs.WriteStringAsync("/local/docs/report.txt", "payload");

        var info = await vfs.GetInfoAsync("/shortcut/report.txt");

        Assert.NotNull(info);
        Assert.True(info.IsAliased);
        Assert.Equal(Path.Combine(_root, "docs", "report.txt"), info.LocalPath);
    }

    [Fact]
    public async Task NodesWithoutAHostPath_ReportNull()
    {
        var vfs = VfsFactory.Build(b => b.Mount("/mem", new InMemoryKvNode()));
        await vfs.WriteStringAsync("/mem/a.txt", "payload");

        var info = await vfs.GetInfoAsync("/mem/a.txt");

        Assert.NotNull(info);
        Assert.Null(info.LocalPath);
    }

    [Fact]
    public async Task Dedupe_ReportsNull_BecauseTheBlobIsNotTheEntry()
    {
        Directory.CreateDirectory(_root);
        var vfs = VfsFactory.Build(b => b
            .Mount("/blobs", new LocalFsNode(_root))
            .Mount("/d", sp => new Nodes.Dedupe.DedupeNode(
                sp.NodeAt("/blobs"), new InMemoryVfsCatalog()), MountLifetime.Singleton));

        await vfs.WriteStringAsync("/d/a.txt", "payload");

        var info = await vfs.GetInfoAsync("/d/a.txt");

        Assert.NotNull(info);
        // The content-addressed blob on disk is not this entry, so there is no honest path to give.
        Assert.Null(info.LocalPath);
    }
}
