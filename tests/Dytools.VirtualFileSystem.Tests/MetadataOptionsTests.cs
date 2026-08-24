using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;

namespace Dytools.VirtualFileSystem.Tests;

// Symlink-capable node with a real listing, a good link and a dangling one.
internal sealed class LinkTestNode : VfsNodeBase, ISymlinkCapableNode
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _links = new(StringComparer.OrdinalIgnoreCase);

    public void Store(string p, string c) => _files[p] = Encoding.UTF8.GetBytes(c);
    public void Link(string p, string t)  => _links[p] = t;

    public override Task<Stream?> OpenReadAsync(VfsNodeRequest r, CancellationToken ct = default)
        => Task.FromResult<Stream?>(_files.TryGetValue(new string(r.Path.PathSpan), out var b)
            ? new MemoryStream(b, false) : null);

    public override Task<Stream> OpenWriteAsync(VfsNodeRequest r, VfsWriteOptions? o = null, CancellationToken ct = default)
        => throw new NotSupportedException();

    public override Task DeleteAsync(VfsNodeRequest r, CancellationToken ct = default) => Task.CompletedTask;

    public override Task<bool> ExistsAsync(VfsNodeRequest r, CancellationToken ct = default)
    {
        var k = new string(r.Path.PathSpan);
        return Task.FromResult(_files.ContainsKey(k) || _links.ContainsKey(k));
    }

    private VfsNodeInfo Info(string key)
    {
        var isLink = _links.TryGetValue(key, out var target);
        return new VfsNodeInfo
        {
            RelativePath  = VfsPath.From(key),
            IsFile        = true,
            IsDirectory   = false,
            IsSymlink     = isLink,
            SymlinkTarget = isLink ? target : null,
        };
    }

    public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest r, CancellationToken ct = default)
    {
        var k = new string(r.Path.PathSpan);
        return Task.FromResult<VfsNodeInfo?>(
            _files.ContainsKey(k) || _links.ContainsKey(k) ? Info(k) : null);
    }

    protected override async IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(
        VfsNodeRequest r, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var k in _files.Keys.Concat(_links.Keys)) yield return Info(k);
    }
}

public sealed class MetadataOptionsTests
{
    private static IVirtualFileSystem Build()
    {
        var node = new LinkTestNode();
        node.Store("real.dat", "content");
        node.Link("good.lnk", "/s/real.dat");
        node.Link("broken.lnk", "/s/gone.dat");
        return VfsFactory.Build(b => b.Mount("/s", node).UseSymlinks());
    }

    // -- Default: stat semantics ----------------------------------------------

    [Fact]
    public async Task Default_GetInfo_FollowsToTheTarget()
    {
        var info = await Build().GetInfoAsync("/s/good.lnk");
        Assert.Equal("real.dat", info!.Name);
    }

    [Fact]
    public async Task Default_DanglingLink_LooksLikeAnAbsentPath()
    {
        var vfs = Build();
        Assert.Null(await vfs.GetInfoAsync("/s/broken.lnk"));
        Assert.False(await vfs.ExistsAsync("/s/broken.lnk"));
    }

    [Fact]
    public async Task Default_ExistsAndGetInfoAgree()
    {
        var vfs = Build();
        foreach (var p in new[] { "/s/real.dat", "/s/good.lnk", "/s/broken.lnk", "/s/absent.dat" })
        {
            var exists = await vfs.ExistsAsync(p);
            var info   = await vfs.GetInfoAsync(p);
            Assert.Equal(exists, info is not null);
        }
    }

    // -- NoFollow: lstat semantics --------------------------------------------

    [Fact]
    public async Task NoFollow_GetInfo_DescribesTheLinkItself()
    {
        var info = await Build().GetInfoAsync("/s/good.lnk", VfsMetadataOptions.NoFollow);
        Assert.Equal("good.lnk", info!.Name);
        Assert.Equal("/s/good.lnk", info.Path);
    }

    [Fact]
    public async Task NoFollow_SeesADanglingLink()
    {
        var vfs = Build();
        Assert.NotNull(await vfs.GetInfoAsync("/s/broken.lnk", VfsMetadataOptions.NoFollow));
        Assert.True(await vfs.ExistsAsync("/s/broken.lnk", VfsMetadataOptions.NoFollow));
    }

    [Fact]
    public async Task NoFollow_DistinguishesADanglingLinkFromAnAbsentPath()
    {
        // The whole point: these two are identical under the default and must not be.
        var vfs = Build();
        Assert.True(await vfs.ExistsAsync("/s/broken.lnk", VfsMetadataOptions.NoFollow));
        Assert.False(await vfs.ExistsAsync("/s/absent.dat", VfsMetadataOptions.NoFollow));
    }

    [Fact]
    public async Task NoFollow_LeavesNonLinksAlone()
    {
        var info = await Build().GetInfoAsync("/s/real.dat", VfsMetadataOptions.NoFollow);
        Assert.Equal("real.dat", info!.Name);
    }

    // -- The context carries one options slot ---------------------------------

    [Fact]
    public void Context_TypedAccessors_OnlyMatchTheirOwnType()
    {
        var listOnly = new VfsListOptions { Recurse = true };
        VfsOperationOptions asBase = listOnly;
        Assert.NotNull(asBase as VfsListOptions);
        Assert.Null(asBase as VfsWriteOptions);
        Assert.Null(asBase as VfsMetadataOptions);
    }
}
