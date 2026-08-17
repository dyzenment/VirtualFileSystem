using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Catalog;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Nodes.Dedupe;
using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Tests;

// The full flat-mount model: a physical backing hidden under an internal /dev, a registered
// JSON catalog, and a dedupe mount that references the backing by path.
public sealed class FlatMountIntegrationTests
{
    private static IVirtualFileSystem Build()
    {
        var services = new ServiceCollection();
        services
            .AddVfsJsonCatalog(sp => sp.NodeAt("/dev/store"))     // catalog persists into the backing
            .AddVirtualFileSystem()
            .MountSingleton<InMemoryKvNode>("/dev/store")         // physical backing (typed mount, no options)
            .SetInternal("/dev")                                  // hidden from direct access
            .MountSingleton<DedupeNode>("/files", o => o.UseSource("/dev/store"));  // dedupe over it, by path
        return services.BuildServiceProvider().GetRequiredService<IVirtualFileSystem>();
    }

    [Fact]
    public async Task WriteReadDedup_ThroughFlatMounts()
    {
        var vfs = Build();

        await VfsFactory.WriteTextAsync(vfs, "/files/a.txt", "hello");
        await VfsFactory.WriteTextAsync(vfs, "/files/b.txt", "hello");   // identical content → dedup

        Assert.Equal("hello", await VfsFactory.ReadTextAsync(vfs, "/files/a.txt"));
        Assert.Equal("hello", await VfsFactory.ReadTextAsync(vfs, "/files/b.txt"));

        // The dedupe catalog is reachable as a capability, and both paths share one blob.
        var catalog = vfs.GetCapability<IVfsCatalog>("/files");
        Assert.NotNull(catalog);
        var id = (await catalog!.GetAsync(VfsPath.From("a.txt")))!.ContentId!;
        Assert.Equal(2, await catalog.ReferenceCountAsync(id));
    }

    [Fact]
    public async Task InternalBacking_HiddenFromDirectAccess()
    {
        var vfs = Build();
        await VfsFactory.WriteTextAsync(vfs, "/files/a.txt", "hello");

        // The blobs really live under /dev/store, but the internal backing is denied to direct access.
        await Assert.ThrowsAnyAsync<Exception>(
            () => VfsFactory.ReadTextAsync(vfs, "/dev/store/a.txt"));
    }
}
