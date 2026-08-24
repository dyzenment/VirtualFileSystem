using System.Security.Cryptography;
using System.Text;
using Dytools.VirtualFileSystem.Nodes.LocalFs;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class LocalFsHashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfs-hash-" + Guid.NewGuid().ToString("N"));

    private IVirtualFileSystem Create()
    {
        Directory.CreateDirectory(_root);
        return VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(_root)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static IContentHashing Hashing(IVirtualFileSystem vfs, string path)
        => vfs.GetEntryCapability<IContentHashing>(path)!;

    [Fact]
    public async Task Sha256_MatchesTheKnownDigest()
    {
        var vfs = Create();
        await vfs.WriteStringAsync("/local/a.txt", "hello vfs");

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("hello vfs")));
        var actual   = await Hashing(vfs, "/local/a.txt").GetHashAsync(VfsHashAlgorithms.Sha256, VfsHashBudget.Compute);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Md5_MatchesTheKnownDigest()
    {
        var vfs = Create();
        await vfs.WriteStringAsync("/local/a.txt", "hello vfs");

        var expected = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("hello vfs")));
        var actual   = await Hashing(vfs, "/local/a.txt").GetHashAsync(VfsHashAlgorithms.Md5, VfsHashBudget.Compute);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task LowercaseHex_SoBackendsCanBeCompared()
    {
        // Encoding is part of the contract: two nodes reporting the same digest differently would
        // never compare equal.
        var vfs = Create();
        await vfs.WriteStringAsync("/local/a.txt", "x");

        var hash = await Hashing(vfs, "/local/a.txt").GetHashAsync(VfsHashAlgorithms.Sha256, VfsHashBudget.Compute);
        Assert.Equal(64, hash!.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.All(hash, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public async Task BelowCompute_Refuses_BecauseEveryAnswerReadsTheFile()
    {
        var vfs = Create();
        await vfs.WriteStringAsync("/local/a.txt", "x");
        var cap = Hashing(vfs, "/local/a.txt");

        Assert.Null(await cap.GetHashAsync(VfsHashAlgorithms.Sha256, VfsHashBudget.Cached));
        Assert.Null(await cap.GetHashAsync(VfsHashAlgorithms.Sha256, VfsHashBudget.Fetch));
    }

    [Fact]
    public async Task ComputeAndStore_DegradesToCompute_WhereThereIsNowhereToStore()
    {
        // A local filesystem has no metadata slot and no catalog, so the store half is a no-op and the
        // answer must still come back rather than the call failing or returning null.
        var vfs = Create();
        await vfs.WriteStringAsync("/local/a.txt", "x");

        var hash = await Hashing(vfs, "/local/a.txt")
            .GetHashAsync(VfsHashAlgorithms.Sha256, VfsHashBudget.ComputeAndStore);
        Assert.False(string.IsNullOrEmpty(hash));
    }

    [Fact]
    public void Advertises_ComputableOnly_NothingNative()
    {
        var cap = Hashing(Create(), "/local/a.txt");
        Assert.Empty(cap.NativeAlgorithms);
        Assert.Contains(VfsHashAlgorithms.Sha256, cap.ComputableAlgorithms);
        Assert.Contains(VfsHashAlgorithms.Md5, cap.ComputableAlgorithms);
    }

    [Fact]
    public async Task UnsupportedAlgorithm_ReturnsNull()
    {
        var vfs = Create();
        await vfs.WriteStringAsync("/local/a.txt", "x");
        Assert.Null(await Hashing(vfs, "/local/a.txt").GetHashAsync(VfsHashAlgorithms.QuickXor, VfsHashBudget.Compute));
    }

    [Fact]
    public async Task MissingFile_ReturnsNull()
        => Assert.Null(await Hashing(Create(), "/local/nope.txt").GetHashAsync(VfsHashAlgorithms.Sha256, VfsHashBudget.Compute));

    [Fact]
    public async Task IdenticalContent_HashesEqual_AcrossDifferentPaths()
    {
        var vfs = Create();
        await vfs.WriteStringAsync("/local/one/a.txt", "same bytes");
        await vfs.WriteStringAsync("/local/two/b.txt", "same bytes");
        await vfs.WriteStringAsync("/local/two/c.txt", "other bytes");

        var a = await Hashing(vfs, "/local/one/a.txt").GetHashAsync(VfsHashAlgorithms.Sha256, VfsHashBudget.Compute);
        var b = await Hashing(vfs, "/local/two/b.txt").GetHashAsync(VfsHashAlgorithms.Sha256, VfsHashBudget.Compute);
        var c = await Hashing(vfs, "/local/two/c.txt").GetHashAsync(VfsHashAlgorithms.Sha256, VfsHashBudget.Compute);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public async Task LargerThanOneBuffer_StillHashesCorrectly()
    {
        var vfs   = Create();
        var bytes = new byte[300_000];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        await vfs.WriteAllBytesAsync("/local/big.bin", bytes);

        var expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(expected, await Hashing(vfs, "/local/big.bin").GetHashAsync(VfsHashAlgorithms.Sha256, VfsHashBudget.Compute));
    }
}

public sealed class TextEncodingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfs-enc-" + Guid.NewGuid().ToString("N"));

    private IVirtualFileSystem Create()
    {
        Directory.CreateDirectory(_root);
        return VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(_root)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task WriteString_DoesNotEmitAByteOrderMark()
    {
        // Encoding.UTF8 would prepend a BOM - invisible on read-back, but three extra bytes to anything
        // hashing or byte-comparing the file.
        var vfs = Create();
        await vfs.WriteStringAsync("/local/a.txt", "hello");

        var bytes = await vfs.ReadAllBytesAsync("/local/a.txt");
        Assert.Equal(Encoding.UTF8.GetBytes("hello"), bytes);
        Assert.Equal(5, bytes!.Length);
    }

    [Fact]
    public async Task WriteString_RoundTrips()
    {
        var vfs = Create();
        await vfs.WriteStringAsync("/local/a.txt", "héllo ✓");
        Assert.Equal("héllo ✓", await vfs.ReadAsStringAsync("/local/a.txt"));
    }
}
