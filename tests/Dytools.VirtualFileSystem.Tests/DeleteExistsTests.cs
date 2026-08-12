namespace Dytools.VirtualFileSystem.Tests;

public sealed class DeleteExistsTests
{
    // -- Exists ----------------------------------------------------------------

    [Fact]
    public async Task Exists_ExistingFile_ReturnsTrue()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "hi");
        Assert.True(await vfs.ExistsAsync("/a/file.txt"));
    }

    [Fact]
    public async Task Exists_MissingFile_ReturnsFalse()
    {
        var vfs = VfsFactory.CreateDual();
        Assert.False(await vfs.ExistsAsync("/a/missing.txt"));
    }

    [Fact]
    public async Task Exists_OnBothNodes_IndependentlyCorrect()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "on-a");
        Assert.True(await vfs.ExistsAsync("/a/file.txt"));
        Assert.False(await vfs.ExistsAsync("/b/file.txt"));
    }

    // -- Delete ----------------------------------------------------------------

    [Fact]
    public async Task Delete_ExistingFile_FileIsGone()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "bye");
        await vfs.DeleteAsync("/a/file.txt");
        Assert.False(await vfs.ExistsAsync("/a/file.txt"));
    }

    [Fact]
    public async Task Delete_ExistingFile_ReadReturnsNull()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "bye");
        await vfs.DeleteAsync("/a/file.txt");
        Assert.Null(await vfs.OpenReadAsync("/a/file.txt"));
    }

    [Fact]
    public async Task Delete_MissingFile_DoesNotThrow()
    {
        var vfs = VfsFactory.CreateDual();
        // InMemoryKvNode silently ignores deletes of missing keys
        await vfs.DeleteAsync("/a/ghost.txt");
    }

    [Fact]
    public async Task Delete_OnlyRemovesTargetFile()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/a.txt", "a");
        await VfsFactory.WriteTextAsync(vfs, "/a/b.txt", "b");
        await vfs.DeleteAsync("/a/a.txt");
        Assert.False(await vfs.ExistsAsync("/a/a.txt"));
        Assert.True(await vfs.ExistsAsync("/a/b.txt"));
    }

    [Fact]
    public async Task Delete_SameFilename_OnSeparateNodes_OnlyRemovesOne()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "on-a");
        await VfsFactory.WriteTextAsync(vfs, "/b/file.txt", "on-b");
        await vfs.DeleteAsync("/a/file.txt");
        Assert.False(await vfs.ExistsAsync("/a/file.txt"));
        Assert.True(await vfs.ExistsAsync("/b/file.txt"));
    }

    [Fact]
    public async Task WriteAfterDelete_ReturnsNewContent()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "old");
        await vfs.DeleteAsync("/a/file.txt");
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "new");
        Assert.Equal("new", await VfsFactory.ReadTextAsync(vfs, "/a/file.txt"));
    }
}
