using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class InternalMountTests
{
    [Fact]
    public async Task InternalMount_DirectAccess_Throws()
    {
        var vfs = VfsFactory.Build(b => b.Mount("/dev/local", new InMemoryKvNode(), isInternal: true));

        await Assert.ThrowsAnyAsync<Exception>(
            () => VfsFactory.WriteTextAsync(vfs, "/dev/local/x.txt", "hi"));
    }

    [Fact]
    public async Task InternalMount_ViaPublicAlias_Works()
    {
        var vfs = VfsFactory.Build(b => b
            .Mount("/dev/local", new InMemoryKvNode(), isInternal: true)
            .Alias("/local", "/dev/local"));          // public door

        await VfsFactory.WriteTextAsync(vfs, "/local/x.txt", "hi");
        Assert.Equal("hi", await VfsFactory.ReadTextAsync(vfs, "/local/x.txt"));
    }

    [Fact]
    public async Task InternalMount_ViaReroute_Works()
    {
        var vfs = VfsFactory.Build(b => b
            .Mount("/dev/local", new InMemoryKvNode(), isInternal: true)
            .Mount("/pub", sp => sp.NodeAt("/dev/local"), MountLifetime.Singleton));

        await VfsFactory.WriteTextAsync(vfs, "/pub/x.txt", "hi");
        Assert.Equal("hi", await VfsFactory.ReadTextAsync(vfs, "/pub/x.txt"));
    }

    [Fact]
    public async Task SetInternal_Prefix_HidesMountsUnderIt()
    {
        var vfs = VfsFactory.Build(b => b
            .Mount("/dev/local", new InMemoryKvNode())   // not explicitly internal…
            .SetInternal("/dev")                          // …but /dev is
            .Alias("/local", "/dev/local"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => VfsFactory.WriteTextAsync(vfs, "/dev/local/x.txt", "hi"));

        await VfsFactory.WriteTextAsync(vfs, "/local/x.txt", "hi");   // via alias works
        Assert.Equal("hi", await VfsFactory.ReadTextAsync(vfs, "/local/x.txt"));
    }

    [Fact]
    public async Task InternalAlias_IsNotAPublicDoor()
    {
        var vfs = VfsFactory.Build(b => b
            .Mount("/dev/local", new InMemoryKvNode(), isInternal: true)
            .Alias("/secret", "/dev/local", isInternal: true));   // internal alias - not sanctioned

        await Assert.ThrowsAnyAsync<Exception>(
            () => VfsFactory.WriteTextAsync(vfs, "/secret/x.txt", "hi"));
    }
}
