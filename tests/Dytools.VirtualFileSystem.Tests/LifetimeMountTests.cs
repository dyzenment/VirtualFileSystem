using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Tests;

// Verifies the two-overload mount API: Mount(node) and Mount(factory, lifetime),
// that scoped/transient nodes resolve against the caller's scope, and NodeAt reroute.
public sealed class LifetimeMountTests
{
    private sealed class ScopeMarker { public Guid Id { get; } = Guid.NewGuid(); }

    private sealed class MarkerNode(ScopeMarker marker) : VfsNodeBase
    {
        public ScopeMarker Marker => marker;

        public override Task<Stream?> OpenReadAsync(VfsNodeRequest r, CancellationToken ct = default)
            => Task.FromResult<Stream?>(null);
        public override Task<Stream> OpenWriteAsync(VfsNodeRequest r, VfsWriteMode m = VfsWriteMode.Create, CancellationToken ct = default)
            => throw new NotSupportedException();
        public override Task DeleteAsync(VfsNodeRequest r, CancellationToken ct = default)
            => Task.CompletedTask;
        public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest r, CancellationToken ct = default)
            => Task.FromResult<VfsNodeInfo?>(null);
        public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(VfsNodeRequest r, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
    }

    private static ServiceProvider Build(MountLifetime lifetime, ServiceLifetime markerLifetime)
    {
        var services = new ServiceCollection();
        switch (markerLifetime)
        {
            case ServiceLifetime.Singleton: services.AddSingleton<ScopeMarker>(); break;
            case ServiceLifetime.Scoped:    services.AddScoped<ScopeMarker>();    break;
            default:                        services.AddTransient<ScopeMarker>(); break;
        }
        services.AddVirtualFileSystem()
            .Mount("/m", sp => new MarkerNode(sp.GetRequiredService<ScopeMarker>()), lifetime);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Scoped_SharesWithinScope_DiffersAcrossScopes()
    {
        var sp = Build(MountLifetime.Scoped, ServiceLifetime.Scoped);

        using var s1 = sp.CreateScope();
        var v1 = s1.ServiceProvider.GetRequiredService<IVirtualFileSystem>();
        var a = v1.GetCapability<MarkerNode>("/m/x")!;
        var b = v1.GetCapability<MarkerNode>("/m/y")!;

        using var s2 = sp.CreateScope();
        var c = s2.ServiceProvider.GetRequiredService<IVirtualFileSystem>().GetCapability<MarkerNode>("/m/z")!;

        Assert.Same(a, b);                  // one scoped node instance per scope, reused
        Assert.Same(a.Marker, b.Marker);
        Assert.NotSame(a, c);               // different scope → different node + marker
        Assert.NotSame(a.Marker, c.Marker);
    }

    [Fact]
    public void Transient_NewNodePerResolve_SharesScopedDependency()
    {
        var sp = Build(MountLifetime.Transient, ServiceLifetime.Scoped);

        using var scope = sp.CreateScope();
        var vfs = scope.ServiceProvider.GetRequiredService<IVirtualFileSystem>();
        var a = vfs.GetCapability<MarkerNode>("/m/x")!;
        var b = vfs.GetCapability<MarkerNode>("/m/y")!;

        Assert.NotSame(a, b);               // transient → new node each resolve
        Assert.Same(a.Marker, b.Marker);    // but the scoped dependency is shared within the scope
    }

    [Fact]
    public void Singleton_SameInstanceEverywhere()
    {
        var sp = Build(MountLifetime.Singleton, ServiceLifetime.Singleton);

        var a = sp.GetRequiredService<IVirtualFileSystem>().GetCapability<MarkerNode>("/m/x")!;
        using var scope = sp.CreateScope();
        var b = scope.ServiceProvider.GetRequiredService<IVirtualFileSystem>().GetCapability<MarkerNode>("/m/y")!;

        Assert.Same(a, b);                  // one instance app-wide
    }

    [Fact]
    public async Task NodeAt_ReroutesToAnotherMount()
    {
        var vfs = VfsFactory.Build(b => b
            .Mount("/a", new InMemoryKvNode())
            .Mount("/b", sp => sp.NodeAt("/a"), MountLifetime.Singleton));

        await VfsFactory.WriteTextAsync(vfs, "/b/x.txt", "hello");

        Assert.Equal("hello", await VfsFactory.ReadTextAsync(vfs, "/a/x.txt"));  // written through to /a
        Assert.Equal("hello", await VfsFactory.ReadTextAsync(vfs, "/b/x.txt"));  // read back via the reroute
    }
}
