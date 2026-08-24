using Dytools.VirtualFileSystem.Nodes.Dedupe;
using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class ContentHashTests
{
    private static IVirtualFileSystem Dedupe()
        => VfsFactory.Build(b => b.Mount("/d", new DedupeNode(new InMemoryKvNode(), new InMemoryVfsCatalog())));

    private static IContentHashing? Hashing(IVirtualFileSystem vfs, string path)
        => vfs.GetEntryCapability<IContentHashing>(path);

    [Fact]
    public async Task Dedupe_ReportsStoredSha256_FromCacheAlone()
    {
        var vfs = Dedupe();
        await vfs.WriteStringAsync("/d/a.txt", "payload");

        // The catalog holds the hash from the write, so the free path must answer.
        var hash = await Hashing(vfs, "/d/a.txt")!.GetHashAsync(VfsHashAlgorithms.Sha256, VfsHashBudget.Cached);
        Assert.False(string.IsNullOrEmpty(hash));
    }

    [Fact]
    public async Task Dedupe_IdenticalContent_HashesEqual_DifferentContent_Differs()
    {
        var vfs = Dedupe();
        await vfs.WriteStringAsync("/d/a.txt", "same");
        await vfs.WriteStringAsync("/d/b.txt", "same");
        await vfs.WriteStringAsync("/d/c.txt", "different");

        var a = await Hashing(vfs, "/d/a.txt")!.GetHashAsync(VfsHashAlgorithms.Sha256);
        var b = await Hashing(vfs, "/d/b.txt")!.GetHashAsync(VfsHashAlgorithms.Sha256);
        var c = await Hashing(vfs, "/d/c.txt")!.GetHashAsync(VfsHashAlgorithms.Sha256);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public async Task Dedupe_UnsupportedAlgorithm_ReturnsNull()
        => Assert.Null(await Hashing(Dedupe(), "/d/a.txt")!.GetHashAsync(VfsHashAlgorithms.QuickXor));

    [Fact]
    public async Task Dedupe_MissingPath_ReturnsNull()
        => Assert.Null(await Hashing(Dedupe(), "/d/nope.txt")!.GetHashAsync(VfsHashAlgorithms.Sha256));

    [Fact]
    public void Dedupe_AdvertisesSha256_BothNativeAndComputable()
    {
        var cap = Hashing(Dedupe(), "/d/a.txt")!;
        Assert.Contains(VfsHashAlgorithms.Sha256, cap.NativeAlgorithms);
        Assert.Contains(VfsHashAlgorithms.Sha256, cap.ComputableAlgorithms);
    }

    [Fact]
    public async Task NodeWithoutTheCapability_ReturnsNullRatherThanThrowing()
    {
        var vfs = VfsFactory.CreateDual();
        await vfs.WriteStringAsync("/a/plain.txt", "x");
        Assert.Null(Hashing(vfs, "/a/plain.txt"));
    }

    // -- The two lookups are distinct ------------------------------------------

    [Fact]
    public void EntryAndNodeLookups_AreSeparate()
    {
        var vfs = Dedupe();

        // Dedupe exposes its catalog at node level and hashing at entry level; neither answers for the
        // other, which is the whole point of the two markers.
        Assert.NotNull(vfs.GetNodeCapability<Catalog.IVfsCatalog>("/d/anything"));
        Assert.NotNull(vfs.GetEntryCapability<IContentHashing>("/d/a.txt"));
    }

    [Fact]
    public async Task EntryCapability_IsBoundToItsEntry()
    {
        // Two capabilities from two paths must answer about their own entries, not share one.
        var vfs = Dedupe();
        await vfs.WriteStringAsync("/d/one.txt", "first");
        await vfs.WriteStringAsync("/d/two.txt", "second");

        var one = await Hashing(vfs, "/d/one.txt")!.GetHashAsync(VfsHashAlgorithms.Sha256);
        var two = await Hashing(vfs, "/d/two.txt")!.GetHashAsync(VfsHashAlgorithms.Sha256);

        Assert.NotNull(one);
        Assert.NotEqual(one, two);
    }

    [Fact]
    public async Task EntryCapability_ResolvesThroughAnAlias()
    {
        // Reached via an alias, the capability must still be bound to the right entry - the consumer
        // never works out where the mount boundary falls.
        var vfs = VfsFactory.Build(b => b
            .Mount("/d", new DedupeNode(new InMemoryKvNode(), new InMemoryVfsCatalog()))
            .Alias("/link", "/d"));

        await vfs.WriteStringAsync("/d/folder/file.txt", "payload");

        var direct  = await Hashing(vfs, "/d/folder/file.txt")!.GetHashAsync(VfsHashAlgorithms.Sha256);
        var aliased = await Hashing(vfs, "/link/folder/file.txt")!.GetHashAsync(VfsHashAlgorithms.Sha256);

        Assert.False(string.IsNullOrEmpty(direct));
        Assert.Equal(direct, aliased);
    }
}
