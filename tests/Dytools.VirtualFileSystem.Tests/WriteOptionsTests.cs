using Dytools.VirtualFileSystem.Nodes.Dedupe;
using Dytools.VirtualFileSystem.Nodes.InMemory;
using Dytools.VirtualFileSystem.Nodes.LocalFs;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class WriteOptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vfs-writeopts-" + Guid.NewGuid().ToString("N"));

    private IVirtualFileSystem CreateLocal()
    {
        Directory.CreateDirectory(_root);
        return VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(_root)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // -- The implicit conversion keeps every existing call site binding ---------

    [Fact]
    public void WriteMode_ConvertsImplicitly_ToOptions()
    {
        VfsWriteOptions options = VfsWriteMode.Append;
        Assert.Equal(VfsWriteMode.Append, options.Mode);
        Assert.Null(options.ModifiedAt);
        Assert.Null(options.CreatedAt);
    }

    [Fact]
    public async Task OpenWrite_WithBareMode_StillHonoursTheMode()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "one");

        await using (var s = await vfs.OpenWriteAsync("/a/file.txt", VfsWriteMode.Append))
        await using (var w = new StreamWriter(s, leaveOpen: true))
            await w.WriteAsync("two");

        Assert.Equal("onetwo", await VfsFactory.ReadTextAsync(vfs, "/a/file.txt"));
    }

    [Fact]
    public async Task Defaults_LeaveTimestampsToTheBackend()
    {
        var vfs    = CreateLocal();
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        await VfsFactory.WriteTextAsync(vfs, "/local/plain.txt", "x");

        var info = await vfs.GetInfoAsync("/local/plain.txt");
        Assert.NotNull(info?.ModifiedAt);
        Assert.True(info!.ModifiedAt >= before, "an unstamped write should carry the backend's own time");
    }

    // -- Requested timestamps are applied, and survive a read back -------------

    [Fact]
    public async Task RequestedModifiedAt_IsAppliedAndReadsBack()
    {
        var vfs      = CreateLocal();
        var modified = new DateTimeOffset(2019, 4, 17, 9, 30, 15, TimeSpan.Zero);

        await using (var s = await vfs.OpenWriteAsync(
                         "/local/stamped.txt", new VfsWriteOptions { ModifiedAt = modified }))
        await using (var w = new StreamWriter(s, leaveOpen: true))
            await w.WriteAsync("contents");

        var info = await vfs.GetInfoAsync("/local/stamped.txt");
        Assert.NotNull(info?.ModifiedAt);
        Assert.Equal(modified.UtcDateTime, info!.ModifiedAt!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RequestedModifiedAt_IsAppliedAfterTheContent()
    {
        // The stamp has to land after the final flush, or the write itself would overwrite it.
        var vfs      = CreateLocal();
        var modified = new DateTimeOffset(2001, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await using (var s = await vfs.OpenWriteAsync(
                         "/local/big.bin", new VfsWriteOptions { ModifiedAt = modified }))
            await s.WriteAsync(new byte[64 * 1024]);

        var info = await vfs.GetInfoAsync("/local/big.bin");
        Assert.Equal(modified.UtcDateTime, info!.ModifiedAt!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.Equal(64 * 1024, info.SizeBytes);
    }

    [Fact]
    public async Task RequestedTimestamps_SurviveSynchronousDispose()
    {
        var vfs      = CreateLocal();
        var modified = new DateTimeOffset(2011, 6, 1, 12, 0, 0, TimeSpan.Zero);

        // Dispose() rather than DisposeAsync() - both paths must stamp.
        using (var s = await vfs.OpenWriteAsync(
                   "/local/sync.txt", new VfsWriteOptions { ModifiedAt = modified }))
            s.Write("data"u8);

        var info = await vfs.GetInfoAsync("/local/sync.txt");
        Assert.Equal(modified.UtcDateTime, info!.ModifiedAt!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RequestedTimestamps_CombineWithMode()
    {
        var vfs      = CreateLocal();
        var modified = new DateTimeOffset(2020, 2, 29, 6, 0, 0, TimeSpan.Zero);

        await VfsFactory.WriteTextAsync(vfs, "/local/appended.txt", "head");

        await using (var s = await vfs.OpenWriteAsync("/local/appended.txt",
                         new VfsWriteOptions { Mode = VfsWriteMode.Append, ModifiedAt = modified }))
        await using (var w = new StreamWriter(s, leaveOpen: true))
            await w.WriteAsync("tail");

        Assert.Equal("headtail", await VfsFactory.ReadTextAsync(vfs, "/local/appended.txt"));
        var info = await vfs.GetInfoAsync("/local/appended.txt");
        Assert.Equal(modified.UtcDateTime, info!.ModifiedAt!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Timestamps_AreIgnoredByBackendsThatCannotSetThem()
    {
        // In-memory has no settable timestamp; asking for one must not fail the write.
        var vfs = VfsFactory.CreateDual();

        await using (var s = await vfs.OpenWriteAsync("/a/mem.txt",
                         new VfsWriteOptions { ModifiedAt = DateTimeOffset.UnixEpoch }))
        await using (var w = new StreamWriter(s, leaveOpen: true))
            await w.WriteAsync("fine");

        Assert.Equal("fine", await VfsFactory.ReadTextAsync(vfs, "/a/mem.txt"));
    }

    // -- Round-tripping through the typed JSON sugar ---------------------------

    [Fact]
    public async Task SendRetrieve_HonourNamingPolicy()
    {
        var vfs  = CreateLocal();
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };

        await vfs.SendAsync("/local/state.json", new Snapshot { LastModified = "x", ItemCount = 3 }, opts);

        var raw = await VfsFactory.ReadTextAsync(vfs, "/local/state.json");
        Assert.Contains("\"lastModified\"", raw);
        Assert.DoesNotContain("\"LastModified\"", raw);

        var round = await vfs.RetrieveAsync<Snapshot>("/local/state.json", opts);
        Assert.Equal("x", round!.LastModified);
        Assert.Equal(3, round.ItemCount);
    }

    private sealed class Snapshot
    {
        public string? LastModified { get; set; }
        public int ItemCount { get; set; }
    }
}

public sealed class DedupeWriteOptionsTests
{
    // Dedupe keeps timestamps as catalog fields rather than backend state, so a requested value is
    // recorded exactly - no backend clock to reconcile against.

    [Fact]
    public async Task Dedupe_RecordsRequestedModifiedAt()
    {
        var (vfs, _) = DedupeFixture.Create();
        var modified = new DateTimeOffset(2015, 7, 4, 11, 22, 33, TimeSpan.Zero);

        await using (var s = await vfs.OpenWriteAsync(
                         "/dedupe/report.txt", new VfsWriteOptions { ModifiedAt = modified }))
        await using (var w = new StreamWriter(s, leaveOpen: true))
            await w.WriteAsync("payload");

        var info = await vfs.GetInfoAsync("/dedupe/report.txt");
        Assert.Equal(modified, info!.ModifiedAt);
    }

    [Fact]
    public async Task Dedupe_RecordsRequestedCreatedAt()
    {
        var (vfs, _) = DedupeFixture.Create();
        var created  = new DateTimeOffset(2013, 3, 3, 3, 3, 3, TimeSpan.Zero);

        await using (var s = await vfs.OpenWriteAsync(
                         "/dedupe/made.txt", new VfsWriteOptions { CreatedAt = created }))
        await using (var w = new StreamWriter(s, leaveOpen: true))
            await w.WriteAsync("payload");

        var info = await vfs.GetInfoAsync("/dedupe/made.txt");
        Assert.Equal(created, info!.CreatedAt);
    }

    [Fact]
    public async Task Dedupe_WithoutRequest_UsesItsOwnClock()
    {
        var (vfs, _) = DedupeFixture.Create();
        var before   = DateTimeOffset.UtcNow.AddSeconds(-5);

        await vfs.WriteStringAsync("/dedupe/plain.txt", "payload");

        var info = await vfs.GetInfoAsync("/dedupe/plain.txt");
        Assert.True(info!.ModifiedAt >= before);
    }

    [Fact]
    public async Task Dedupe_TimestampIsPerPath_NotPerContent()
    {
        // Two paths sharing one deduplicated blob must still carry their own timestamps.
        var (vfs, _) = DedupeFixture.Create();
        var first    = new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var second   = new DateTimeOffset(2022, 12, 25, 0, 0, 0, TimeSpan.Zero);

        await using (var a = await vfs.OpenWriteAsync("/dedupe/a.txt", new VfsWriteOptions { ModifiedAt = first }))
        await using (var w = new StreamWriter(a, leaveOpen: true))
            await w.WriteAsync("identical");

        await using (var b = await vfs.OpenWriteAsync("/dedupe/b.txt", new VfsWriteOptions { ModifiedAt = second }))
        await using (var w = new StreamWriter(b, leaveOpen: true))
            await w.WriteAsync("identical");

        Assert.Equal(first,  (await vfs.GetInfoAsync("/dedupe/a.txt"))!.ModifiedAt);
        Assert.Equal(second, (await vfs.GetInfoAsync("/dedupe/b.txt"))!.ModifiedAt);
    }
}

// A dedupe mount over an in-memory blob store, for asserting catalog-recorded timestamps.
internal static class DedupeFixture
{
    public static (IVirtualFileSystem Vfs, InMemoryVfsCatalog Catalog) Create()
    {
        var catalog = new InMemoryVfsCatalog();
        var node    = new DedupeNode(new InMemoryKvNode(), catalog);
        var vfs     = VfsFactory.Build(b => b.Mount("/dedupe", node));
        return (vfs, catalog);
    }
}
