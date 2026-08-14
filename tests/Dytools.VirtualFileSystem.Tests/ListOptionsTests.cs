using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class ListOptionsTests
{
    private static async Task<IVirtualFileSystem> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddVirtualFileSystem().MountSingleton<InMemoryKvNode>("/mem");
        var vfs = services.BuildServiceProvider().GetRequiredService<IVirtualFileSystem>();
        await VfsFactory.WriteTextAsync(vfs, "/mem/root.txt",        "r");
        await VfsFactory.WriteTextAsync(vfs, "/mem/docs/a.pdf",      "a");
        await VfsFactory.WriteTextAsync(vfs, "/mem/docs/b.txt",      "b");
        await VfsFactory.WriteTextAsync(vfs, "/mem/docs/deep/c.pdf", "c");
        return vfs;
    }

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> src)
    {
        var list = new List<string>();
        await foreach (var s in src) list.Add(s);
        return list;
    }

    [Fact]
    public async Task Kind_FilesOnly_And_DirectoriesOnly()
    {
        var vfs = await BuildAsync();

        var files = await Collect(vfs.ListAsync("/mem", new VfsListOptions { Kind = VfsEntryKind.Files }));
        Assert.Contains("/mem/root.txt", files);
        Assert.DoesNotContain("/mem/docs", files);

        var dirs = await Collect(vfs.ListAsync("/mem", new VfsListOptions { Kind = VfsEntryKind.Directories }));
        Assert.Contains("/mem/docs", dirs);
        Assert.DoesNotContain("/mem/root.txt", dirs);
    }

    [Fact]
    public async Task Recurse_WithMaxDepth_BoundsDescent()
    {
        var vfs = await BuildAsync();

        // MaxDepth = 1: immediate children only (level 1).
        var d1 = await Collect(vfs.ListAsync("/mem", new VfsListOptions { Recurse = true, MaxDepth = 1 }));
        Assert.Contains("/mem/docs", d1);
        Assert.DoesNotContain("/mem/docs/a.pdf", d1);

        // MaxDepth = 2: one level deeper, but not level 3.
        var d2 = await Collect(vfs.ListAsync("/mem", new VfsListOptions { Recurse = true, MaxDepth = 2 }));
        Assert.Contains("/mem/docs/a.pdf", d2);          // level 2
        Assert.DoesNotContain("/mem/docs/deep/c.pdf", d2); // level 3 - excluded

        // Unlimited: the whole subtree.
        var all = await Collect(vfs.ListAsync("/mem", new VfsListOptions { Recurse = true }));
        Assert.Contains("/mem/docs/deep/c.pdf", all);
    }

    [Fact]
    public async Task SearchPattern_RecursivelyMatchesLeafNames()
    {
        var vfs = await BuildAsync();

        var pdfs = await Collect(vfs.ListAsync("/mem",
            new VfsListOptions { Recurse = true, SearchPattern = "*.pdf" }));

        Assert.Contains("/mem/docs/a.pdf",      pdfs);
        Assert.Contains("/mem/docs/deep/c.pdf", pdfs);
        Assert.DoesNotContain("/mem/docs/b.txt", pdfs);
    }

    [Fact]
    public async Task StrictMode_Throws_WhenNodeCannotPushDown()
    {
        var node = new ScanGuardNode();
        var req  = new VfsNodeRequest(VfsPath.From(""));

        // Suffix pattern → the node reports a full scan → strict mode throws.
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in node.ListAsync(req,
                new VfsListOptions { SearchPattern = "*.pdf", ThrowIfPatternNotSupported = true })) { }
        });

        // A pure prefix is pushable → no throw.
        await foreach (var _ in node.ListAsync(req,
            new VfsListOptions { SearchPattern = "report*", ThrowIfPatternNotSupported = true })) { }
    }

    // Mimics a prefix-only backend (S3/Azure): a non-prefix pattern forces a full scan.
    private sealed class ScanGuardNode : VfsNodeBase
    {
        protected override bool RequiresFullScan(VfsListOptions options)
            => !IsPurePrefixPattern(options.SearchPattern);

        protected override async IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(
            VfsNodeRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }

        public override Task<Stream?> OpenReadAsync(VfsNodeRequest r, CancellationToken ct = default) => Task.FromResult<Stream?>(null);
        public override Task<Stream>  OpenWriteAsync(VfsNodeRequest r, VfsWriteMode m = VfsWriteMode.Create, CancellationToken ct = default) => throw new NotSupportedException();
        public override Task          DeleteAsync(VfsNodeRequest r, CancellationToken ct = default) => Task.CompletedTask;
        public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest r, CancellationToken ct = default) => Task.FromResult<VfsNodeInfo?>(null);
    }
}
