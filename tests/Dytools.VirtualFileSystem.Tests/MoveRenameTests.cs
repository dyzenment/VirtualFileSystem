namespace Dytools.VirtualFileSystem.Tests;

public sealed class MoveRenameTests
{
    // -- Move same-node --------------------------------------------------------

    [Fact]
    public async Task Move_SameNode_DestinationHasContent()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/src.txt", "data");
        await vfs.MoveAsync("/a/src.txt", "/a/dst.txt");
        Assert.Equal("data", await VfsFactory.ReadTextAsync(vfs, "/a/dst.txt"));
    }

    [Fact]
    public async Task Move_SameNode_SourceIsRemoved()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/src.txt", "data");
        await vfs.MoveAsync("/a/src.txt", "/a/dst.txt");
        Assert.False(await vfs.ExistsAsync("/a/src.txt"));
    }

    [Fact]
    public async Task Move_SameNode_OverwritesExistingDestination()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/src.txt", "new");
        await VfsFactory.WriteTextAsync(vfs, "/a/dst.txt", "old");
        await vfs.MoveAsync("/a/src.txt", "/a/dst.txt");
        Assert.Equal("new", await VfsFactory.ReadTextAsync(vfs, "/a/dst.txt"));
    }

    // -- Move cross-node -------------------------------------------------------

    [Fact]
    public async Task Move_CrossNode_DestinationHasContent()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/src.txt", "cross-data");
        await vfs.MoveAsync("/a/src.txt", "/b/dst.txt");
        Assert.Equal("cross-data", await VfsFactory.ReadTextAsync(vfs, "/b/dst.txt"));
    }

    [Fact]
    public async Task Move_CrossNode_SourceIsRemoved()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/src.txt", "cross-data");
        await vfs.MoveAsync("/a/src.txt", "/b/dst.txt");
        Assert.False(await vfs.ExistsAsync("/a/src.txt"));
    }

    [Fact]
    public async Task Move_CrossNode_ContentIsIntact()
    {
        var vfs   = VfsFactory.CreateDual();
        var bytes = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        await using (var s = await vfs.OpenWriteAsync("/a/bin.dat"))
            await s.WriteAsync(bytes);

        await vfs.MoveAsync("/a/bin.dat", "/b/bin.dat");

        var result = await VfsFactory.ReadBytesAsync(vfs, "/b/bin.dat");
        Assert.Equal(bytes, result);
    }

    // -- Rename (always same-directory, same-node) -----------------------------

    [Fact]
    public async Task Rename_NewNameHasContent()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/old.txt", "data");
        await vfs.RenameAsync("/a/old.txt", "new.txt");
        Assert.Equal("data", await VfsFactory.ReadTextAsync(vfs, "/a/new.txt"));
    }

    [Fact]
    public async Task Rename_OldNameIsGone()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/old.txt", "data");
        await vfs.RenameAsync("/a/old.txt", "new.txt");
        Assert.False(await vfs.ExistsAsync("/a/old.txt"));
    }

    [Fact]
    public async Task Rename_StaysInSameDirectory()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/sub/file.txt", "nested");
        await vfs.RenameAsync("/a/sub/file.txt", "renamed.txt");
        Assert.Equal("nested", await VfsFactory.ReadTextAsync(vfs, "/a/sub/renamed.txt"));
        Assert.False(await vfs.ExistsAsync("/a/sub/file.txt"));
    }

    [Fact]
    public async Task Rename_DoesNotAffectSiblingFiles()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/a.txt", "a");
        await VfsFactory.WriteTextAsync(vfs, "/a/b.txt", "b");
        await vfs.RenameAsync("/a/a.txt", "c.txt");
        Assert.Equal("b", await VfsFactory.ReadTextAsync(vfs, "/a/b.txt"));
    }
}
