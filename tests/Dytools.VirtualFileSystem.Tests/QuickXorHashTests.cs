using System.Text;

namespace Dytools.VirtualFileSystem.Tests;

// NOTE: these pin the implementation's own consistency. They cannot confirm it agrees with what
// SharePoint computes - that needs one real quickXorHash from a live item to compare against.
public sealed class QuickXorHashTests
{
    private static string Hash(params string[] chunks)
    {
        var h = new QuickXorHash();
        foreach (var c in chunks) h.Append(Encoding.UTF8.GetBytes(c));
        return h.Finish();
    }

    [Fact]
    public void SameInput_SameHash()
        => Assert.Equal(Hash("the quick brown fox"), Hash("the quick brown fox"));

    [Fact]
    public void DifferentInput_DifferentHash()
        => Assert.NotEqual(Hash("the quick brown fox"), Hash("the quick brown fix"));

    [Fact]
    public void Streaming_MatchesOneShot()
    {
        // The rotating shift carries between calls; if it did not, chunked input would diverge.
        Assert.Equal(Hash("the quick brown fox jumps over the lazy dog"),
                     Hash("the quick ", "brown fox ", "jumps over ", "the lazy dog"));
    }

    [Fact]
    public void ChunkBoundaries_DoNotChangeTheResult()
    {
        var whole = new string('x', 1000) + "tail";
        var oneShot = Hash(whole);

        for (var size = 1; size <= 257; size += 64)
        {
            var h = new QuickXorHash();
            for (var i = 0; i < whole.Length; i += size)
                h.Append(Encoding.UTF8.GetBytes(whole.Substring(i, Math.Min(size, whole.Length - i))));
            Assert.Equal(oneShot, h.Finish());
        }
    }

    [Fact]
    public void TrailingZeroBytes_AreDistinguished()
    {
        // A plain XOR would miss these; mixing the length into the tail is what catches them.
        var a = new QuickXorHash(); a.Append(new byte[] { 1, 2, 3 });
        var b = new QuickXorHash(); b.Append(new byte[] { 1, 2, 3, 0 });
        Assert.NotEqual(a.Finish(), b.Finish());
    }

    [Fact]
    public void Reordering_IsDetected()
    {
        // The rotating offset is the reason - a positionless XOR would give these the same value.
        var a = new QuickXorHash(); a.Append(new byte[] { 1, 2 });
        var b = new QuickXorHash(); b.Append(new byte[] { 2, 1 });
        Assert.NotEqual(a.Finish(), b.Finish());
    }

    [Fact]
    public void Empty_ProducesAStableValue()
        => Assert.Equal(new QuickXorHash().Finish(), new QuickXorHash().Finish());

    [Fact]
    public void ProducesA160BitBase64Value()
    {
        var bytes = Convert.FromBase64String(Hash("anything"));
        Assert.Equal(20, bytes.Length);
    }

    [Fact]
    public async Task StreamOverload_MatchesTheIncrementalForm()
    {
        var payload = Encoding.UTF8.GetBytes(new string('q', 5000));
        using var stream = new MemoryStream(payload);

        var viaStream = await QuickXorHash.ComputeAsync(stream);

        var direct = new QuickXorHash();
        direct.Append(payload);

        Assert.Equal(direct.Finish(), viaStream);
    }

    [Fact]
    public async Task ReachableThroughTheHashingCapability()
    {
        // The point of it being in core: a local file can produce the one algorithm SharePoint reports.
        var root = Path.Combine(Path.GetTempPath(), "vfs-qx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var vfs = VfsFactory.Build(b => b.Mount("/local", new Nodes.LocalFs.LocalFsNode(root)));
            await vfs.WriteStringAsync("/local/a.txt", "compare me");

            var cap = vfs.GetEntryCapability<IContentHashing>("/local/a.txt")!;
            Assert.Contains(VfsHashAlgorithms.QuickXor, cap.ComputableAlgorithms);

            var hash = await cap.GetHashAsync(VfsHashAlgorithms.QuickXor, VfsHashBudget.Compute);
            Assert.Equal(Hash("compare me"), hash);
        }
        finally { try { Directory.Delete(root, true); } catch { /* best effort */ } }
    }
}
