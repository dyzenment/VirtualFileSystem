using Dytools.VirtualFileSystem.Nodes.InMemory;
using Dytools.VirtualFileSystem.Nodes.LocalFs;
using Dytools.VirtualFileSystem.Nodes.LocalFs.Trash;

namespace Dytools.VirtualFileSystem.Tests;

/// <summary>
/// Disposition behaviour, driven through a stub bin so the assertions are the same on every OS and
/// nothing lands in the developer's real Trash. The platform providers themselves are covered by
/// <see cref="RecycleBinPlatformTests"/>.
/// </summary>
public sealed class DeleteDispositionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vfs-recycle-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    // A bin that records what it was handed instead of touching the OS.
    private sealed class FakeTrash(bool canRecycle = true, Exception? failWith = null) : ITrashProvider
    {
        public List<string> Trashed { get; } = [];
        public bool IsSupported => true;
        public bool CanRecycle(string physicalPath) => canRecycle;

        public string? Trash(string physicalPath)
        {
            if (failWith is not null) throw failWith;
            Trashed.Add(physicalPath);
            File.Delete(physicalPath);          // stand in for "it moved somewhere else"
            return "/fake/trash/" + Path.GetFileName(physicalPath);
        }
    }

    private LocalFsNode Node(ITrashProvider trash) => new(_root) { TrashProvider = trash };

    private string Seed(string name, string content = "bytes")
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static VfsNodeRequest Req(string relative) => new(VfsPath.From(relative));

    // -- Permanent (the default, unchanged behaviour) --------------------------

    [Fact]
    public async Task Default_DeletesPermanently_AndNeverTouchesTheBin()
    {
        var trash = new FakeTrash();
        var path  = Seed("file.txt");

        await Node(trash).DeleteAsync(Req("file.txt"));

        Assert.False(File.Exists(path));
        Assert.Empty(trash.Trashed);
    }

    [Fact]
    public async Task Permanent_OnMissingEntry_IsStillANoOp()
    {
        await Node(new FakeTrash()).DeleteAsync(Req("ghost.txt"));
    }

    // -- Recycle ---------------------------------------------------------------

    [Fact]
    public async Task Recycle_SendsTheEntryToTheBin()
    {
        var trash = new FakeTrash();
        var path  = Seed("file.txt");

        await Node(trash).DeleteAsync(Req("file.txt"), VfsDeleteOptions.Recycle);

        Assert.Equal([path], trash.Trashed);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Recycle_WhenTheVolumeHasNoBin_ThrowsRatherThanDestroying()
    {
        var trash = new FakeTrash(canRecycle: false);
        var path  = Seed("file.txt");

        await Assert.ThrowsAsync<NotSupportedException>(
            () => Node(trash).DeleteAsync(Req("file.txt"), VfsDeleteOptions.Recycle));

        // The whole point of the strict mode: the caller's bytes are still there.
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Recycle_WhenTheBinItselfFails_Propagates()
    {
        var trash = new FakeTrash(failWith: new IOException("bin is full"));
        var path  = Seed("file.txt");

        await Assert.ThrowsAsync<IOException>(
            () => Node(trash).DeleteAsync(Req("file.txt"), VfsDeleteOptions.Recycle));

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Recycle_OnMissingEntry_DoesNotThrowEvenWithoutABin()
    {
        // Nothing to recycle means nothing to refuse - matching the long-standing no-op on a
        // missing path.
        await Node(new FakeTrash(canRecycle: false))
            .DeleteAsync(Req("ghost.txt"), VfsDeleteOptions.Recycle);
    }

    // -- RecycleIfAvailable ----------------------------------------------------

    [Fact]
    public async Task RecycleIfAvailable_UsesTheBinWhenThereIsOne()
    {
        var trash = new FakeTrash();
        var path  = Seed("file.txt");

        await Node(trash).DeleteAsync(Req("file.txt"), VfsDeleteOptions.RecycleIfAvailable);

        Assert.Equal([path], trash.Trashed);
    }

    [Fact]
    public async Task RecycleIfAvailable_FallsBackWhenTheVolumeHasNoBin()
    {
        var trash = new FakeTrash(canRecycle: false);
        var path  = Seed("file.txt");

        await Node(trash).DeleteAsync(Req("file.txt"), VfsDeleteOptions.RecycleIfAvailable);

        Assert.Empty(trash.Trashed);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RecycleIfAvailable_FallsBackWhenTheBinFailsMidOperation()
    {
        // CanRecycle only vets the volume; a read-only mount or an over-quota bin only shows up on
        // the attempt itself, and this mode asked for a permanent delete in exactly that case.
        var trash = new FakeTrash(failWith: new UnauthorizedAccessException());
        var path  = Seed("file.txt");

        await Node(trash).DeleteAsync(Req("file.txt"), VfsDeleteOptions.RecycleIfAvailable);

        Assert.False(File.Exists(path));
    }

    // -- Recursion -------------------------------------------------------------

    [Fact]
    public async Task Recursive_ByDefault_TakesTheWholeTree()
    {
        Seed("tree/nested/leaf.txt");

        await Node(new FakeTrash()).DeleteAsync(Req("tree"));

        Assert.False(Directory.Exists(Path.Combine(_root, "tree")));
    }

    [Fact]
    public async Task NonRecursive_OnANonEmptyDirectory_Throws()
    {
        Seed("tree/nested/leaf.txt");

        await Assert.ThrowsAsync<IOException>(
            () => Node(new FakeTrash()).DeleteAsync(Req("tree"), new VfsDeleteOptions { Recursive = false }));

        Assert.True(Directory.Exists(Path.Combine(_root, "tree")));
    }

    // -- Move must not recycle its own source ---------------------------------

    [Fact]
    public async Task Move_DeletesTheSourcePermanently_NeverThroughTheBin()
    {
        // The base MoveAsync is copy + delete. If that delete inherited a recycle disposition, every
        // move would leave a copy of itself in the bin for the user to clean up.
        var trash = new FakeTrash();
        var inner = new InMemoryKvNode();
        var node  = new MoveViaCopyNode(inner, trash);

        await using (var w = await node.OpenWriteAsync(Req("src.txt")))
            await w.WriteAsync("payload"u8.ToArray());

        await node.MoveAsync(Req("src.txt"), Req("dst.txt"));

        Assert.Empty(trash.Trashed);
        Assert.Null(await node.OpenReadAsync(Req("src.txt")));
        Assert.NotNull(await node.OpenReadAsync(Req("dst.txt")));
    }

    // Wraps an in-memory node, recording any delete that arrives asking to recycle.
    private sealed class MoveViaCopyNode(InMemoryKvNode inner, FakeTrash trash) : VfsNodeBase
    {
        public override Task<Stream?> OpenReadAsync(VfsNodeRequest r, CancellationToken ct = default)
            => inner.OpenReadAsync(r, ct);

        public override Task<Stream> OpenWriteAsync(
            VfsNodeRequest r, VfsWriteOptions? o = null, CancellationToken ct = default)
            => inner.OpenWriteAsync(r, o, ct);

        public override Task DeleteAsync(
            VfsNodeRequest r, VfsDeleteOptions? o = null, CancellationToken ct = default)
        {
            if ((o ?? VfsDeleteOptions.Default).Disposition != VfsDeleteDisposition.Permanent)
                trash.Trashed.Add(new string(r.Path.PathSpan));
            return inner.DeleteAsync(r, VfsDeleteOptions.Default, ct);
        }

        public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest r, CancellationToken ct = default)
            => inner.GetInfoAsync(r, ct);

        protected override IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(VfsNodeRequest r, CancellationToken ct)
            => inner.ListAsync(r, VfsListOptions.Default, ct);
    }
}
