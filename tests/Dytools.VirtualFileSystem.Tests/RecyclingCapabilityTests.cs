using Dytools.VirtualFileSystem.Nodes.InMemory;
using Dytools.VirtualFileSystem.Nodes.LocalFs;

namespace Dytools.VirtualFileSystem.Tests;

/// <summary>
/// The <see cref="IRecycling"/> capability, and how the disposition reaches a node through the
/// pipeline rather than by direct call.
/// </summary>
public sealed class RecyclingCapabilityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vfs-recycle-cap-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private IVirtualFileSystem Vfs()
        => VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(_root))
                                  .Mount("/mem",   new InMemoryKvNode()));

    [Fact]
    public void LocalFs_ExposesTheCapability()
    {
        Assert.NotNull(Vfs().GetEntryCapability<IRecycling>("/local/anything.txt"));
    }

    [Fact]
    public void InMemory_DoesNot()
    {
        // A null capability and a capability answering false mean the same thing to a caller, but a
        // backend with no bin at all should not be handing one out.
        Assert.Null(Vfs().GetEntryCapability<IRecycling>("/mem/anything.txt"));
    }

    [Fact]
    public async Task LocalFs_CapabilityAnswersForTheEntrysOwnVolume()
    {
        var capability = Vfs().GetEntryCapability<IRecycling>("/local/anything.txt");

        // The answer is whatever this machine's volume supports - the contract under test is that
        // asking is cheap, does not throw, and does not require the entry to exist.
        var canRecycle = await capability!.CanRecycleAsync();
        Assert.Equal(canRecycle, await capability.CanRecycleAsync());
    }

    [Fact]
    public void LocalFs_StillExposesHashing()
    {
        // Regression: adding a second capability to the dispatch must not shadow the first.
        Assert.NotNull(Vfs().GetEntryCapability<IContentHashing>("/local/anything.txt"));
    }

    // -- Disposition through the pipeline --------------------------------------

    [Fact]
    public async Task Recycle_ThroughThePipeline_ReachesTheNode()
    {
        var vfs = Vfs();
        await VfsFactory.WriteTextAsync(vfs, "/mem/file.txt", "hi");

        // InMemoryKvNode has no bin, so a strict Recycle must surface as a refusal rather than being
        // dropped somewhere between DefaultVirtualFileSystem and the node.
        await Assert.ThrowsAsync<NotSupportedException>(
            () => vfs.DeleteAsync("/mem/file.txt", VfsDeleteOptions.Recycle));

        Assert.True(await vfs.ExistsAsync("/mem/file.txt"));
    }

    [Fact]
    public async Task RecycleIfAvailable_ThroughThePipeline_DegradesToPermanent()
    {
        var vfs = Vfs();
        await VfsFactory.WriteTextAsync(vfs, "/mem/file.txt", "hi");

        await vfs.DeleteAsync("/mem/file.txt", VfsDeleteOptions.RecycleIfAvailable);

        Assert.False(await vfs.ExistsAsync("/mem/file.txt"));
    }

    [Fact]
    public async Task BareDisposition_ConvertsImplicitly()
    {
        var vfs = Vfs();
        await VfsFactory.WriteTextAsync(vfs, "/mem/file.txt", "hi");

        await vfs.DeleteAsync("/mem/file.txt", VfsDeleteDisposition.RecycleIfAvailable);

        Assert.False(await vfs.ExistsAsync("/mem/file.txt"));
    }

    [Fact]
    public async Task NullOptions_MeansPermanent()
    {
        var vfs = Vfs();
        await VfsFactory.WriteTextAsync(vfs, "/mem/file.txt", "hi");

        await vfs.DeleteAsync("/mem/file.txt", options: null);

        Assert.False(await vfs.ExistsAsync("/mem/file.txt"));
    }

    // -- ResolveRecycle, the helper every node routes through ------------------

    [Theory]
    [InlineData(VfsDeleteDisposition.Permanent,          true,  false)]
    [InlineData(VfsDeleteDisposition.Permanent,          false, false)]
    [InlineData(VfsDeleteDisposition.Recycle,            true,  true)]
    [InlineData(VfsDeleteDisposition.RecycleIfAvailable, true,  true)]
    [InlineData(VfsDeleteDisposition.RecycleIfAvailable, false, false)]
    public void ResolveRecycle_SettlesTheRequestAgainstWhatTheBackendCanDo(
        VfsDeleteDisposition disposition, bool available, bool expected)
    {
        VfsDeleteOptions options = disposition;
        Assert.Equal(expected, options.ResolveRecycle(available, VfsPath.From("/x/y.txt")));
    }

    [Fact]
    public void ResolveRecycle_StrictRecycleWithNoBin_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => VfsDeleteOptions.Recycle.ResolveRecycle(available: false, VfsPath.From("/x/y.txt")));

        Assert.Contains("/x/y.txt", ex.Message);
        Assert.Contains(nameof(VfsDeleteDisposition.RecycleIfAvailable), ex.Message);
    }
}
