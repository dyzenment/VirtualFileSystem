namespace Dytools.VirtualFileSystem.Tests;

public sealed class CopyTests
{
    // -- Same-node -------------------------------------------------------------

    [Fact]
    public async Task Copy_SameNode_DestinationHasSourceContent()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/src.txt", "hello");
        await vfs.CopyAsync("/a/src.txt", "/a/dst.txt");
        Assert.Equal("hello", await VfsFactory.ReadTextAsync(vfs, "/a/dst.txt"));
    }

    [Fact]
    public async Task Copy_SameNode_SourceStillExists()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/src.txt", "hello");
        await vfs.CopyAsync("/a/src.txt", "/a/dst.txt");
        Assert.Equal("hello", await VfsFactory.ReadTextAsync(vfs, "/a/src.txt"));
    }

    [Fact]
    public async Task Copy_SameNode_DestinationIsIndependentCopy()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/src.txt", "original");
        await vfs.CopyAsync("/a/src.txt", "/a/dst.txt");
        await VfsFactory.WriteTextAsync(vfs, "/a/src.txt", "modified");
        Assert.Equal("original", await VfsFactory.ReadTextAsync(vfs, "/a/dst.txt"));
    }

    [Fact]
    public async Task Copy_SameNode_OverwritesExistingDestination()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/src.txt", "new");
        await VfsFactory.WriteTextAsync(vfs, "/a/dst.txt", "old");
        await vfs.CopyAsync("/a/src.txt", "/a/dst.txt");
        Assert.Equal("new", await VfsFactory.ReadTextAsync(vfs, "/a/dst.txt"));
    }

    // -- Cross-node ------------------------------------------------------------

    [Fact]
    public async Task Copy_CrossNode_DestinationHasSourceContent()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/src.txt", "cross-copy");
        await vfs.CopyAsync("/a/src.txt", "/b/dst.txt");
        Assert.Equal("cross-copy", await VfsFactory.ReadTextAsync(vfs, "/b/dst.txt"));
    }

    [Fact]
    public async Task Copy_CrossNode_SourceStillExists()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/src.txt", "cross-copy");
        await vfs.CopyAsync("/a/src.txt", "/b/dst.txt");
        Assert.Equal("cross-copy", await VfsFactory.ReadTextAsync(vfs, "/a/src.txt"));
    }

    [Fact]
    public async Task Copy_CrossNode_DestinationDoesNotExistOnSourceNode()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/src.txt", "data");
        await vfs.CopyAsync("/a/src.txt", "/b/dst.txt");
        // dst only lives on /b - should not appear on /a
        Assert.Null(await VfsFactory.ReadTextAsync(vfs, "/a/dst.txt"));
    }

    [Fact]
    public async Task Copy_CrossNode_BinaryContentIsPreserved()
    {
        var vfs    = VfsFactory.CreateDual();
        var bytes  = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await using (var s = await vfs.OpenWriteAsync("/a/bin.dat"))
            await s.WriteAsync(bytes);

        await vfs.CopyAsync("/a/bin.dat", "/b/bin.dat");

        var result = await VfsFactory.ReadBytesAsync(vfs, "/b/bin.dat");
        Assert.Equal(bytes, result);
    }

    [Fact]
    public async Task Copy_SourceNotFound_DestinationDoesNotExist()
    {
        var vfs = VfsFactory.CreateDual();
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => vfs.CopyAsync("/a/ghost.txt", "/b/ghost.txt"));
    }
}
