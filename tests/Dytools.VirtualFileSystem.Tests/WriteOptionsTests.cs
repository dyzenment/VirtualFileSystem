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
