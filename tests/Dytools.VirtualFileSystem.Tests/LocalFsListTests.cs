using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Nodes.LocalFs;

namespace Dytools.VirtualFileSystem.Tests;

// Exercises LocalFsNode's native ListAsync override (FileSystemEnumerable-based recursion,
// pattern, kind) against a real temp directory tree.
public sealed class LocalFsListTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vfs-localfs-list-" + Guid.NewGuid().ToString("N"));

    public LocalFsListTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "deep"));
        File.WriteAllText(Path.Combine(_root, "root.txt"),              "r");
        File.WriteAllText(Path.Combine(_root, "docs", "a.pdf"),        "a");
        File.WriteAllText(Path.Combine(_root, "docs", "b.txt"),        "b");
        File.WriteAllText(Path.Combine(_root, "docs", "deep", "c.pdf"), "c");
    }

    private IVirtualFileSystem Build()
    {
        var services = new ServiceCollection();
        services.AddVirtualFileSystem().Mount("/fs", new LocalFsNode(_root));
        return services.BuildServiceProvider().GetRequiredService<IVirtualFileSystem>();
    }

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> src)
    {
        var list = new List<string>();
        await foreach (var s in src) list.Add(s);
        return list;
    }

    [Fact]
    public async Task ImmediateChildren_ByDefault()
    {
        var paths = await Collect(Build().ListAsync("/fs"));
        Assert.Contains("/fs/root.txt", paths);
        Assert.Contains("/fs/docs",     paths);
        Assert.DoesNotContain("/fs/docs/a.pdf", paths);
    }

    [Fact]
    public async Task Recursive_WholeTree()
    {
        var paths = await Collect(Build().ListAsync("/fs", new VfsListOptions { Recurse = true }));
        Assert.Contains("/fs/docs/a.pdf",      paths);
        Assert.Contains("/fs/docs/deep/c.pdf", paths);
    }

    [Fact]
    public async Task Recursive_SearchPattern_PushedDown()
    {
        var paths = await Collect(Build().ListAsync("/fs",
            new VfsListOptions { Recurse = true, SearchPattern = "*.pdf" }));

        Assert.Contains("/fs/docs/a.pdf",      paths);
        Assert.Contains("/fs/docs/deep/c.pdf", paths);
        Assert.DoesNotContain("/fs/docs/b.txt", paths);
        Assert.DoesNotContain("/fs/root.txt",   paths);
    }

    [Fact]
    public async Task Recursive_DirectoriesOnly()
    {
        var paths = await Collect(Build().ListAsync("/fs",
            new VfsListOptions { Recurse = true, Kind = VfsEntryKind.Directories }));

        Assert.Contains("/fs/docs",      paths);
        Assert.Contains("/fs/docs/deep", paths);
        Assert.DoesNotContain("/fs/docs/a.pdf", paths);
    }

    [Fact]
    public async Task MaxDepth_BoundsDescent()
    {
        var d1 = await Collect(Build().ListAsync("/fs", new VfsListOptions { Recurse = true, MaxDepth = 1 }));
        Assert.Contains("/fs/docs", d1);
        Assert.DoesNotContain("/fs/docs/a.pdf", d1);   // level 2 excluded

        var d2 = await Collect(Build().ListAsync("/fs", new VfsListOptions { Recurse = true, MaxDepth = 2 }));
        Assert.Contains("/fs/docs/a.pdf", d2);
        Assert.DoesNotContain("/fs/docs/deep/c.pdf", d2);   // level 3 excluded
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
