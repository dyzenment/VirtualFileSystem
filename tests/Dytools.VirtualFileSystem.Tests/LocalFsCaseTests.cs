using Dytools.VirtualFileSystem.Nodes.LocalFs;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class LocalFsCaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfs-case-" + Guid.NewGuid().ToString("N"));

    private IVirtualFileSystem Create(bool? caseSensitive)
    {
        Directory.CreateDirectory(_root);
        return VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(_root, caseSensitive)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<List<string>> Names(IVirtualFileSystem vfs, string pattern)
    {
        var names = new List<string>();
        await foreach (var e in vfs.ListInfoAsync("/local", new VfsListOptions { SearchPattern = pattern }))
            names.Add(e.Name);
        return names;
    }

    // The native LocalFs listing calls FileSystemName.MatchesSimpleExpression, whose ignoreCase
    // parameter defaults to true - so before this it matched case-insensitively on every platform,
    // including case-sensitive ones, and IsCaseSensitive was never consulted.

    [Fact]
    public async Task SearchPattern_CaseSensitiveMount_DoesNotMatchOnCase()
    {
        var vfs = Create(caseSensitive: true);
        await vfs.WriteStringAsync("/local/report.txt", "x");

        Assert.Empty(await Names(vfs, "*.TXT"));
        Assert.Single(await Names(vfs, "*.txt"));
    }

    [Fact]
    public async Task SearchPattern_CaseInsensitiveMount_Matches()
    {
        var vfs = Create(caseSensitive: false);
        await vfs.WriteStringAsync("/local/report.txt", "x");

        Assert.Single(await Names(vfs, "*.TXT"));
        Assert.Single(await Names(vfs, "*.txt"));
    }

    [Fact]
    public void HostPathMapping_CaseSensitiveMount_RejectsMismatchedCasing()
    {
        var vfs = Create(caseSensitive: true);
        var shouted = Path.Combine(_root.ToUpperInvariant(), "report.txt");

        Assert.False(vfs.TryGetVfsPath(shouted, out _));
        Assert.True(vfs.TryGetVfsPath(Path.Combine(_root, "report.txt"), out _));
    }

    [Fact]
    public void HostPathMapping_CaseInsensitiveMount_AcceptsMismatchedCasing()
    {
        var vfs = Create(caseSensitive: false);
        var shouted = Path.Combine(_root.ToUpperInvariant(), "report.txt");

        Assert.True(vfs.TryGetVfsPath(shouted, out var vfsPath));
        Assert.Equal("/local/report.txt", vfsPath);
    }

    [Fact]
    public void PlatformDefault_MatchesTheRunningOs()
    {
        var vfs = Create(caseSensitive: null);
        var expectedInsensitive = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        var shouted = Path.Combine(_root.ToUpperInvariant(), "report.txt");

        Assert.Equal(expectedInsensitive, vfs.TryGetVfsPath(shouted, out _));
    }

    [Fact]
    public void MountOptions_CarryTheSetting()
    {
        Directory.CreateDirectory(_root);
        var options = new VfsMountOptions().UseLocalFileSystemPath(_root);
        options.Require<LocalFsOptions>().CaseSensitive = true;

        var vfs = VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(options)));
        Assert.False(vfs.TryGetVfsPath(Path.Combine(_root.ToUpperInvariant(), "a.txt"), out _));
    }
}
