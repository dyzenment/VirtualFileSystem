namespace Dytools.VirtualFileSystem.Tests;

public sealed class AliasTests
{
    private static IVirtualFileSystem BuildWithAlias()
        => VfsFactory.Build(b => b
            .Mount("/real", new Dytools.VirtualFileSystem.Nodes.InMemory.InMemoryKvNode())
            .Alias("/alias", "/real"));

    // -- Read / Write through alias --------------------------------------------

    [Fact]
    public async Task Alias_WriteViaAlias_ReadableViaRealPath()
    {
        var vfs = BuildWithAlias();
        await VfsFactory.WriteTextAsync(vfs, "/alias/file.txt", "aliased");
        Assert.Equal("aliased", await VfsFactory.ReadTextAsync(vfs, "/real/file.txt"));
    }

    [Fact]
    public async Task Alias_WriteViaRealPath_ReadableViaAlias()
    {
        var vfs = BuildWithAlias();
        await VfsFactory.WriteTextAsync(vfs, "/real/file.txt", "direct");
        Assert.Equal("direct", await VfsFactory.ReadTextAsync(vfs, "/alias/file.txt"));
    }

    // -- IsAliased flag --------------------------------------------------------

    [Fact]
    public async Task Alias_GetInfo_IsAliased_TrueWhenAccessedViaAlias()
    {
        var vfs = BuildWithAlias();
        await VfsFactory.WriteTextAsync(vfs, "/real/file.txt", "x");
        var info = await vfs.GetInfoAsync("/alias/file.txt");
        Assert.True(info?.IsAliased);
    }

    [Fact]
    public async Task Alias_GetInfo_IsAliased_FalseWhenAccessedDirectly()
    {
        var vfs = BuildWithAlias();
        await VfsFactory.WriteTextAsync(vfs, "/real/file.txt", "x");
        var info = await vfs.GetInfoAsync("/real/file.txt");
        Assert.False(info?.IsAliased);
    }

    [Fact]
    public async Task Alias_GetInfo_IsSymlink_FalseForAliasedPath()
    {
        var vfs = BuildWithAlias();
        await VfsFactory.WriteTextAsync(vfs, "/real/file.txt", "x");
        var info = await vfs.GetInfoAsync("/alias/file.txt");
        Assert.False(info?.IsSymlink);
    }

    // -- Exists / Delete through alias -----------------------------------------

    [Fact]
    public async Task Alias_Exists_TrueWhenAccessedViaAlias()
    {
        var vfs = BuildWithAlias();
        await VfsFactory.WriteTextAsync(vfs, "/real/file.txt", "y");
        Assert.True(await vfs.ExistsAsync("/alias/file.txt"));
    }

    [Fact]
    public async Task Alias_Delete_ViaAlias_RemovesFromRealPath()
    {
        var vfs = BuildWithAlias();
        await VfsFactory.WriteTextAsync(vfs, "/real/file.txt", "del-me");
        await vfs.DeleteAsync("/alias/file.txt");
        Assert.False(await vfs.ExistsAsync("/real/file.txt"));
    }

    // -- Copy / Move through alias ---------------------------------------------

    [Fact]
    public async Task Alias_Copy_SrcViaAlias_LandsOnRealNode()
    {
        var vfs = BuildWithAlias();
        await VfsFactory.WriteTextAsync(vfs, "/alias/src.txt", "copy-me");
        await vfs.CopyAsync("/alias/src.txt", "/alias/dst.txt");
        Assert.Equal("copy-me", await VfsFactory.ReadTextAsync(vfs, "/real/dst.txt"));
    }

    // -- Instance mounting and unmounting --------------------------------------

    [Fact]
    public async Task Mount_InstanceMount_FilesAccessible()
    {
        var vfs  = VfsFactory.CreateDual();
        var node = new Dytools.VirtualFileSystem.Nodes.InMemory.InMemoryKvNode();
        vfs.Mount("/dyn", node);
        await VfsFactory.WriteTextAsync(vfs, "/dyn/file.txt", "dynamic");
        Assert.Equal("dynamic", await VfsFactory.ReadTextAsync(vfs, "/dyn/file.txt"));
    }

    [Fact]
    public async Task Unmount_FilesNoLongerResolvable()
    {
        var vfs  = VfsFactory.CreateDual();
        var node = new Dytools.VirtualFileSystem.Nodes.InMemory.InMemoryKvNode();
        vfs.Mount("/dyn", node);
        await VfsFactory.WriteTextAsync(vfs, "/dyn/file.txt", "dynamic");
        vfs.Unmount("/dyn");
        // After unmount the mount is gone - registry should throw or return null
        await Assert.ThrowsAnyAsync<Exception>(() => vfs.OpenReadAsync("/dyn/file.txt"));
    }
}
