using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Nodes.InMemory;
using Dytools.VirtualFileSystem.Nodes.LocalFs;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class TypedMountTests
{
    [Fact]
    public async Task MountSingleton_LocalFs_ActivatesFromOptions()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var vfs = VfsFactory.Build(b =>
                b.MountSingleton<LocalFsNode>("/disk", o => o.UseLocalFileSystemPath(dir)));

            await VfsFactory.WriteTextAsync(vfs, "/disk/hello.txt", "hi");

            Assert.Equal("hi", await VfsFactory.ReadTextAsync(vfs, "/disk/hello.txt"));
            Assert.True(File.Exists(Path.Combine(dir, "hello.txt")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task MountSingleton_ParameterlessNode_NoOptionsNeeded()
    {
        var vfs = VfsFactory.Build(b => b.MountSingleton<InMemoryKvNode>("/mem"));

        await VfsFactory.WriteTextAsync(vfs, "/mem/x.txt", "data");
        Assert.Equal("data", await VfsFactory.ReadTextAsync(vfs, "/mem/x.txt"));
    }

    [Fact]
    public async Task MountSingleton_SameInstance_AcrossResolves()
    {
        var vfs = VfsFactory.Build(b => b.MountSingleton<InMemoryKvNode>("/mem"));

        // Written data persists across operations → the node instance is held (singleton).
        await VfsFactory.WriteTextAsync(vfs, "/mem/a.txt", "one");
        Assert.Equal("one", await VfsFactory.ReadTextAsync(vfs, "/mem/a.txt"));
    }

    [Fact]
    public void MountSingleton_MissingRequiredOptions_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddVirtualFileSystem().MountSingleton<LocalFsNode>("/disk");   // forgot UseLocalFileSystemPath
        var vfs = services.BuildServiceProvider().GetRequiredService<IVirtualFileSystem>();

        Assert.ThrowsAny<Exception>(() => vfs.GetCapability<object>("/disk/x"));
    }
}
