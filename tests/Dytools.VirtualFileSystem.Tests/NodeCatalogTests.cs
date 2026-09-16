using Dytools.VirtualFileSystem.Catalog;
using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Tests;

// The shared catalog-mirror plumbing used by the S3, Azure, and SharePoint caching nodes.
public sealed class CatalogMirrorTests
{
    private static NodeCatalog NewMirror() => new(new JsonFileVfsCatalog(new InMemoryKvNode()));

    private static VfsNodeInfo File(string path, long size = 1) => new()
    {
        RelativePath = VfsPath.From(path),
        IsFile       = true,
        IsDirectory  = false,
        SizeBytes    = size,
        ModifiedAt   = DateTimeOffset.UnixEpoch,
    };

    private static async Task<List<string>> Children(NodeCatalog m, string path)
    {
        var list = new List<string>();
        await foreach (var e in m.ListChildrenAsync(VfsPath.From(path))) list.Add(e.Path.ToString());
        return list;
    }

    [Fact]
    public async Task Upsert_Then_Serve_ImmediateChildren()
    {
        var m = NewMirror();
        await m.UpsertAsync(File("docs/a.txt"));
        await m.UpsertAsync(File("docs/b.txt"));

        var kids = await Children(m, "docs");
        Assert.Contains("docs/a.txt", kids);
        Assert.Contains("docs/b.txt", kids);
        Assert.Equal(2, kids.Count);
    }

    [Fact]
    public async Task Remove_Deletes_From_Mirror()
    {
        var m = NewMirror();
        await m.UpsertAsync(File("docs/a.txt"));
        await m.RemoveAsync(VfsPath.From("docs/a.txt"));

        Assert.Empty(await Children(m, "docs"));
    }

    [Fact]
    public async Task Move_ReKeys_Only_When_Present()
    {
        var m = NewMirror();
        await m.UpsertAsync(File("a.txt"));

        await m.MoveAsync(VfsPath.From("a.txt"), VfsPath.From("b.txt"));
        var root = await Children(m, "");
        Assert.Contains("b.txt", root);
        Assert.DoesNotContain("a.txt", root);

        // Moving something absent is a no-op (doesn't throw).
        await m.MoveAsync(VfsPath.From("ghost.txt"), VfsPath.From("x.txt"));
        Assert.DoesNotContain("x.txt", await Children(m, ""));
    }

    [Fact]
    public async Task State_RoundTrips_And_Is_Hidden_From_Listings()
    {
        var m = NewMirror();
        await m.UpsertAsync(File("a.txt"));
        await m.SetStateAsync("seeded", "1");
        await m.SetStateAsync("cursor", "abc");

        Assert.Equal("1",   await m.GetStateAsync("seeded"));
        Assert.Equal("abc", await m.GetStateAsync("cursor"));   // second key preserved

        // The reserved state entry never shows up in a listing.
        var root = await Children(m, "");
        Assert.Contains("a.txt", root);
        Assert.DoesNotContain(".vfs-mirror-state", root);
        Assert.Single(root);
    }

    [Fact]
    public async Task Clear_Wipes_Entries_But_Keeps_State()
    {
        var m = NewMirror();
        await m.UpsertAsync(File("docs/a.txt"));
        await m.UpsertAsync(File("b.txt"));
        await m.SetStateAsync("seeded", "1");

        await m.ClearAsync();

        Assert.Empty(await Children(m, ""));
        Assert.Equal("1", await m.GetStateAsync("seeded"));   // survives the wipe
    }

    [Fact]
    public async Task SetIfNull_SetsWhenAbsent_ThenBlocks_UntilCleared()
    {
        var m = NewMirror();
        Assert.True(await m.SetIfNullStateAsync("lease", "A"));
        Assert.Equal("A", await m.GetStateAsync("lease"));
        Assert.False(await m.SetIfNullStateAsync("lease", "B"));   // already held
        Assert.Equal("A", await m.GetStateAsync("lease"));         // not overwritten

        await m.ClearStateAsync("lease");
        Assert.Null(await m.GetStateAsync("lease"));
        Assert.True(await m.SetIfNullStateAsync("lease", "C"));     // free again
    }

    [Fact]
    public async Task SetIfNull_AtMostOneWinner_UnderConcurrency()
    {
        // Contenders race over one linearizable catalog; the splitter must let AT MOST ONE win. A loser
        // may return false or, under sustained contention, throw TimeoutException - both count as "did
        // not win". Shrink the ADB backoff so contended rounds don't sleep the real 2-7s.
        var (min, max) = (NodeCatalog.BackoffMinMs, NodeCatalog.BackoffMaxMs);
        NodeCatalog.BackoffMinMs = NodeCatalog.BackoffMaxMs = 1;
        try
        {
            for (var round = 0; round < 100; round++)
            {
                var m = new NodeCatalog(new InMemoryVfsCatalog());
                var tasks = Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
                {
                    try   { return await m.SetIfNullStateAsync("lease", $"owner{i}"); }
                    catch (TimeoutException) { return false; }
                })).ToArray();

                var winners = (await Task.WhenAll(tasks)).Count(won => won);
                Assert.True(winners <= 1, $"round {round}: {winners} winners");
            }
        }
        finally { (NodeCatalog.BackoffMinMs, NodeCatalog.BackoffMaxMs) = (min, max); }
    }

    [Fact]
    public async Task SetIfNull_Cancellation_RollsBackItsWrite()
    {
        var cts   = new CancellationTokenSource();
        var inner = new InMemoryVfsCatalog();
        var m     = new NodeCatalog(new CancelAfterKeyWrite(inner, "lease", cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => m.SetIfNullStateAsync("lease", "A", cts.Token));

        // The write it made before cancelling was rolled back - the key is free.
        Assert.Null(await m.GetStateAsync("lease"));
    }

    // Cancels the supplied token right after the value entry for `key` is written (splitter step 4),
    // so we can exercise the cancel-rollback path deterministically.
    private sealed class CancelAfterKeyWrite(IContentAddressedCatalog inner, string key, CancellationTokenSource cts) : IContentAddressedCatalog
    {
        public async ValueTask<CatalogEntry?> PutEntryAsync(CatalogEntry entry, CancellationToken ct = default)
        {
            var prev = await inner.PutEntryAsync(entry, ct);
            if (entry.Path.ToString().EndsWith("/" + key, StringComparison.Ordinal)) cts.Cancel();
            return prev;
        }

        public ValueTask<CatalogEntry?> GetAsync(VfsPath p, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return inner.GetAsync(p, ct); }   // observe the cancel we just fired
        public IAsyncEnumerable<CatalogEntry> ListChildrenAsync(VfsPath p, CancellationToken ct = default) => inner.ListChildrenAsync(p, ct);
        public ValueTask<int> ReferenceCountAsync(string id, CancellationToken ct = default) => inner.ReferenceCountAsync(id, ct);
        public ValueTask<string?> FindContentIdByHashAsync(string h, CancellationToken ct = default) => inner.FindContentIdByHashAsync(h, ct);
        public ValueTask EnsureDirectoryAsync(VfsPath p, DateTimeOffset t, CancellationToken ct = default) => inner.EnsureDirectoryAsync(p, t, ct);
        public IAsyncEnumerable<CatalogEntry> RemoveAsync(VfsPath p, CancellationToken ct = default) => inner.RemoveAsync(p, ct);
        public ValueTask MoveAsync(VfsPath f, VfsPath t, CancellationToken ct = default) => inner.MoveAsync(f, t, ct);
        public ValueTask TouchAccessedAsync(VfsPath p, DateTimeOffset a, CancellationToken ct = default) => inner.TouchAccessedAsync(p, a, ct);
    }

    [Fact]
    public async Task CatalogClear_Default_KeepsNestedSubtreeAndItsAncestors_RemovesTheRest()
    {
        IVfsCatalog catalog = new JsonFileVfsCatalog(new InMemoryKvNode());
        foreach (var path in new[] { "a/b/keep.txt", "a/c/gone.txt", "a/gone.txt", "d/gone.txt", "top.txt" })
            await catalog.PutEntryAsync(new CatalogEntry { Path = VfsPath.From(path), IsDirectory = false });

        await catalog.ClearAsync([VfsPath.From("a/b"), VfsPath.From("")]);   // the root is ignored, not "keep everything"

        var left = new List<string>();
        var pending = new Stack<VfsPath>([VfsPath.From("")]);
        while (pending.Count > 0)
            await foreach (var e in catalog.ListChildrenAsync(pending.Pop()))
            {
                left.Add(e.Path.ToString());
                if (e.IsDirectory) pending.Push(e.Path);
            }

        Assert.Equal(new[] { "a", "a/b", "a/b/keep.txt" }, left.Order());
    }

    [Fact]
    public async Task NodeCatalogClear_HandsTheWholeWipeToTheCatalog_KeepingOnlyTheStateDirectory()
    {
        var catalog = new ClearRecordingCatalog();
        var m = new NodeCatalog(catalog);

        await m.ClearAsync();

        var keep = Assert.Single(catalog.Clears);
        Assert.Equal(".vfs-mirror-state", Assert.Single(keep).ToString());
        Assert.Empty(catalog.BulkRemoves);   // not asked to remove top-level folders one by one
    }

    // Overrides the wipe, as a database-backed catalog would, and records what it was asked to do.
    private sealed class ClearRecordingCatalog : IVfsCatalog
    {
        private readonly IVfsCatalog _inner = new JsonFileVfsCatalog(new InMemoryKvNode());
        public List<IReadOnlyCollection<VfsPath>> Clears { get; } = new();
        public List<IEnumerable<VfsPath>> BulkRemoves { get; } = new();

        public ValueTask ClearAsync(IReadOnlyCollection<VfsPath> keep, CancellationToken ct = default)
        { Clears.Add(keep); return ValueTask.CompletedTask; }
        public ValueTask RemoveAsync(IEnumerable<VfsPath> paths, CancellationToken ct = default)
        { BulkRemoves.Add(paths); return ValueTask.CompletedTask; }

        public ValueTask<CatalogEntry?> GetAsync(VfsPath p, CancellationToken ct = default) => _inner.GetAsync(p, ct);
        public IAsyncEnumerable<CatalogEntry> ListChildrenAsync(VfsPath p, CancellationToken ct = default) => _inner.ListChildrenAsync(p, ct);
        public ValueTask<CatalogEntry?> PutEntryAsync(CatalogEntry e, CancellationToken ct = default) => _inner.PutEntryAsync(e, ct);
        public ValueTask EnsureDirectoryAsync(VfsPath p, DateTimeOffset t, CancellationToken ct = default) => _inner.EnsureDirectoryAsync(p, t, ct);
        public IAsyncEnumerable<CatalogEntry> RemoveAsync(VfsPath p, CancellationToken ct = default) => _inner.RemoveAsync(p, ct);
        public ValueTask MoveAsync(VfsPath f, VfsPath t, CancellationToken ct = default) => _inner.MoveAsync(f, t, ct);
    }

    [Fact]
    public void ToNodeInfo_Maps_Fields()
    {
        var entry = new CatalogEntry
        {
            Path = VfsPath.From("docs/a.txt"), IsDirectory = false, Size = 42,
            ModifiedAt = DateTimeOffset.UnixEpoch, ContentType = "text/plain",
            Properties = new Dictionary<string, string?> { ["ETag"] = "e1" },
        };

        var info = NodeCatalog.ToNodeInfo(entry);
        Assert.True(info.IsFile);
        Assert.Equal(42, info.SizeBytes);
        Assert.Equal("e1", info.Properties.GetString("ETag"));
    }
}
