using Dytools.VirtualFileSystem.Nodes.Dedupe;
using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Tests;

// The budget is a ceiling, not a strategy: each level authorises everything below it, and a node
// answers as cheaply as it can.
public sealed class HashBudgetTests
{
    private static (IVirtualFileSystem Vfs, InMemoryVfsCatalog Catalog) Dedupe()
    {
        var catalog = new InMemoryVfsCatalog();
        var vfs = VfsFactory.Build(b => b.Mount("/d", new DedupeNode(new InMemoryKvNode(), catalog)));
        return (vfs, catalog);
    }

    private static IContentHashing Hashing(IVirtualFileSystem vfs, string path)
        => vfs.GetEntryCapability<IContentHashing>(path)!;

    [Fact]
    public void DefaultBudget_IsFetch_SoNothingDownloadsByAccident()
    {
        // The guard that keeps a loop over a listing from pulling every file's content.
        Assert.Equal(VfsHashBudget.Fetch, default(VfsHashBudget) + 1);
        Assert.True(VfsHashBudget.Fetch < VfsHashBudget.Compute);
        Assert.True(VfsHashBudget.Compute < VfsHashBudget.ComputeAndStore);
    }

    [Fact]
    public async Task Cached_AnswersTheStoredIdentityHash()
    {
        var (vfs, _) = Dedupe();
        await vfs.WriteStringAsync("/d/a.txt", "payload");

        // Dedupe writes sha256 to the row on save, so even the zero-cost level has it.
        Assert.False(string.IsNullOrEmpty(
            await Hashing(vfs, "/d/a.txt").GetHashAsync(VfsHashAlgorithms.Sha256, VfsHashBudget.Cached)));
    }

    [Fact]
    public async Task Cached_RefusesAnAlgorithmItWouldHaveToRead()
    {
        var (vfs, _) = Dedupe();
        await vfs.WriteStringAsync("/d/a.txt", "payload");

        Assert.Null(await Hashing(vfs, "/d/a.txt").GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.Cached));
        Assert.Null(await Hashing(vfs, "/d/a.txt").GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.Fetch));
    }

    [Fact]
    public async Task Compute_ReadsTheContentForAnAlgorithmNotOnTheRow()
    {
        var (vfs, _) = Dedupe();
        await vfs.WriteStringAsync("/d/a.txt", "payload");

        var md5 = await Hashing(vfs, "/d/a.txt").GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.Compute);
        Assert.False(string.IsNullOrEmpty(md5));
    }

    [Fact]
    public async Task Compute_CachesLocally_SoTheNextCallIsFree()
    {
        // The catalog is the node's own cache: writing to it is not a mutation of anyone else's data,
        // so it happens at Compute rather than needing ComputeAndStore.
        var (vfs, _) = Dedupe();
        await vfs.WriteStringAsync("/d/a.txt", "payload");

        var computed = await Hashing(vfs, "/d/a.txt")
            .GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.Compute);
        var cached = await Hashing(vfs, "/d/a.txt")
            .GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.Cached);

        Assert.False(string.IsNullOrEmpty(computed));
        Assert.Equal(computed, cached);
    }

    [Fact]
    public async Task ComputeAndStore_MatchesCompute_WhereThereIsNoServiceToWriteTo()
    {
        // Dedupe has no backing service, so the extra level has nothing further to do.
        var (vfs, _) = Dedupe();
        await vfs.WriteStringAsync("/d/a.txt", "payload");

        var viaCompute = await Hashing(vfs, "/d/a.txt")
            .GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.Compute);
        var viaStore = await Hashing(vfs, "/d/a.txt")
            .GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.ComputeAndStore);

        Assert.False(string.IsNullOrEmpty(viaCompute));
        Assert.Equal(viaCompute, viaStore);
    }

    [Fact]
    public async Task ComputeAndStore_AlsoMakesTheNextCallFree()
    {
        var (vfs, _) = Dedupe();
        await vfs.WriteStringAsync("/d/a.txt", "payload");

        var computed = await Hashing(vfs, "/d/a.txt")
            .GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.ComputeAndStore);

        // Now on the row, so the zero-cost level answers - and with the same value.
        var cached = await Hashing(vfs, "/d/a.txt")
            .GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.Cached);

        Assert.False(string.IsNullOrEmpty(computed));
        Assert.Equal(computed, cached);
    }

    [Fact]
    public async Task StoredHash_IsDiscardedWhenTheEntryChanges()
    {
        // The row is the unit of invalidation: rewriting the entry rebuilds it, and the cached hash
        // goes with it rather than lingering as a wrong answer.
        var (vfs, _) = Dedupe();
        await vfs.WriteStringAsync("/d/a.txt", "first");
        await Hashing(vfs, "/d/a.txt").GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.ComputeAndStore);

        await vfs.WriteStringAsync("/d/a.txt", "second");

        Assert.Null(await Hashing(vfs, "/d/a.txt").GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.Cached));
    }

    [Fact]
    public async Task StoredHash_MatchesAFreshComputationOfTheNewContent()
    {
        var (vfs, _) = Dedupe();
        await vfs.WriteStringAsync("/d/a.txt", "first");
        await Hashing(vfs, "/d/a.txt").GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.ComputeAndStore);

        await vfs.WriteStringAsync("/d/a.txt", "second");
        var after = await Hashing(vfs, "/d/a.txt")
            .GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.ComputeAndStore);

        // Same content, computed independently - proves the second answer is not the stale first one.
        await vfs.WriteStringAsync("/d/b.txt", "second");
        var independent = await Hashing(vfs, "/d/b.txt")
            .GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.Compute);

        Assert.Equal(independent, after);
    }
}
