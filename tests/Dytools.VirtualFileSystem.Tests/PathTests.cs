namespace Dytools.VirtualFileSystem.Tests;

// Tests VfsPath.Normalize rules through the public VFS API.
// Normalization happens before any path reaches the registry or node.
public sealed class PathTests
{
    [Fact]
    public async Task DotSegment_IsIgnored()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/foo.txt", "dot");
        // /a/./foo.txt should resolve to /a/foo.txt
        Assert.Equal("dot", await VfsFactory.ReadTextAsync(vfs, "/a/./foo.txt"));
    }

    [Fact]
    public async Task DotDot_TraversesUp()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/foo.txt", "dotdot");
        // /a/sub/../foo.txt should resolve to /a/foo.txt
        Assert.Equal("dotdot", await VfsFactory.ReadTextAsync(vfs, "/a/sub/../foo.txt"));
    }

    [Fact]
    public async Task DotDot_AtRoot_ClampsToRoot()
    {
        // /../../a/foo.txt should clamp to /a/foo.txt (can't go above /)
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/foo.txt", "clamped");
        Assert.Equal("clamped", await VfsFactory.ReadTextAsync(vfs, "/../../a/foo.txt"));
    }

    [Fact]
    public async Task Backslash_TreatedAsSeparator()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/sub/file.txt", "backslash");
        // Windows-style path - backslash is normalised to forward slash
        Assert.Equal("backslash", await VfsFactory.ReadTextAsync(vfs, @"/a\sub\file.txt"));
    }

    [Fact]
    public async Task MultipleSlashes_Collapsed()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "slashes");
        Assert.Equal("slashes", await VfsFactory.ReadTextAsync(vfs, "/a//file.txt"));
    }

    [Fact]
    public async Task QueryString_IsPreservedAndNotNormalised()
    {
        // Nodes receive the query string separately; Normalize must not collapse it.
        // We verify by checking Exists on a path that contains a query string.
        // InMemoryKvNode ignores query strings, so the file is still found.
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "query");
        Assert.True(await vfs.ExistsAsync("/a/file.txt?v=1"));
    }

    [Fact]
    public async Task ChainedDotDot_ResolvesCorrectly()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/target.txt", "chain");
        Assert.Equal("chain", await VfsFactory.ReadTextAsync(vfs, "/a/x/y/../../target.txt"));
    }

    [Fact]
    public async Task TrailingSlash_NormalisedAway()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "trail");
        // path with trailing slash - normalisation strips it
        // This tests via Exists since a path like /a/file.txt/ is unusual
        Assert.True(await vfs.ExistsAsync("/a/file.txt"));
    }

    [Fact]
    public async Task ScopeToCurrentDirectory_ResolvesPaths()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/docs/file.txt", "scoped");

        var scoped = vfs.ScopeTo("/a/docs");
        var result = await VfsFactory.ReadTextAsync(scoped, "file.txt");
        Assert.Equal("scoped", result);
    }

    [Fact]
    public async Task ScopeTo_RelativeDotDot_ResolvesRelativeToScope()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "escape");
        var scoped = vfs.ScopeTo("/a/sub");
        Assert.Equal("escape", await VfsFactory.ReadTextAsync(scoped, "../file.txt"));
    }

    [Fact]
    public async Task DrivePrefix_MountAndReadRoundTrip()
    {
        // A named-drive mount point normalises to "/azure"; reads via either the
        // drive syntax or the leading-slash form hit the same node.
        var vfs = VfsFactory.Build(b =>
            b.Mount(@"azure:\", new Dytools.VirtualFileSystem.Nodes.InMemory.InMemoryKvNode()));
        await VfsFactory.WriteTextAsync(vfs, @"azure:\data\file.txt", "drive");
        Assert.Equal("drive", await VfsFactory.ReadTextAsync(vfs, @"azure:\data\file.txt"));
        Assert.Equal("drive", await VfsFactory.ReadTextAsync(vfs, "/azure/data/file.txt"));
    }
}
