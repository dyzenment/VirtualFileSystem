using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class AutoInitTests
{
    [Fact]
    public async Task Mounts_And_Aliases_Work_Without_Calling_Initialize()
    {
        var services = new ServiceCollection();
        services.AddVirtualFileSystem()
            .Mount("/mem", new InMemoryKvNode())
            .Alias("/docs", "/mem/documents");

        var sp = services.BuildServiceProvider();

        // Deliberately NOT calling sp.InitializeVirtualFileSystem() - the registry must
        // self-populate from options on first use.
        await using var vfs = sp.GetRequiredService<IVirtualFileSystem>();

        await using (var w = await vfs.OpenWriteAsync("/mem/documents/a.txt"))
            await w.WriteAsync(Encoding.UTF8.GetBytes("hi"));

        Assert.True(await vfs.ExistsAsync("/mem/documents/a.txt"));
        Assert.True(await vfs.ExistsAsync("/docs/a.txt"));   // alias resolves too
    }
}
