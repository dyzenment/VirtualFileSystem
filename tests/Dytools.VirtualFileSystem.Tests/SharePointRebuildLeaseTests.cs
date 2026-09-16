using System.Net;
using System.Text;
using Dytools.VirtualFileSystem.Catalog;
using Dytools.VirtualFileSystem.Nodes.InMemory;
using Dytools.VirtualFileSystem.Nodes.SharePoint;

namespace Dytools.VirtualFileSystem.Tests;

[CollectionDefinition(nameof(SharePointRebuildLeaseTests), DisableParallelization = true)]
public sealed class SharePointSyncTimingCollection;

// The rebuild's catalog clear has no delta pages to renew the lease between. These shrink the lease so a
// clear that takes a fraction of a second outlasts it, the way a database clear of a whole drive outlasts
// the real 30 seconds. The timings are process-wide, so this class runs alone.
[Collection(nameof(SharePointRebuildLeaseTests))]
public sealed class SharePointRebuildLeaseTests : IDisposable
{
    private const string Base  = "https://graph.microsoft.com/v1.0/";
    private const string Delta = """
        {"value":[{"id":"01A","name":"a.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}],
        "@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/drive1/root/delta?token=DONE"}
        """;

    private readonly (TimeSpan Ttl, TimeSpan Margin, TimeSpan Renew) _saved =
        (SharePointNode.SyncTtl, SharePointNode.SyncAbortMargin, SharePointNode.SyncRenewInterval);

    public SharePointRebuildLeaseTests()
    {
        SharePointNode.SyncTtl           = TimeSpan.FromMilliseconds(300);
        SharePointNode.SyncAbortMargin   = TimeSpan.FromMilliseconds(50);
        SharePointNode.SyncRenewInterval = TimeSpan.FromMilliseconds(50);
    }

    public void Dispose()
        => (SharePointNode.SyncTtl, SharePointNode.SyncAbortMargin, SharePointNode.SyncRenewInterval) = _saved;

    [Fact]
    public async Task Rebuild_WhoseClearOutlastsTheLease_KeepsTheLeaseAndFinishes()
    {
        var catalog = new SlowClearCatalog(new JsonFileVfsCatalog(new InMemoryKvNode()),
            during: (_, ct) => Task.Delay(TimeSpan.FromMilliseconds(900), ct));
        var (node, mirror) = Node(catalog);
        await mirror.UpsertAsync(new VfsNodeInfo { RelativePath = VfsPath.From("stale.txt"), IsFile = true, IsDirectory = false });

        await node.RefreshAsync();   // without renewal the first page reads as an overrun and this throws

        Assert.True(catalog.LeaseWritesDuringClear >= 3, $"lease renewed {catalog.LeaseWritesDuringClear} time(s)");
        Assert.Null(await mirror.GetAsync(VfsPath.From("stale.txt")));
        Assert.NotNull(await mirror.GetAsync(VfsPath.From("a.txt")));
        Assert.Null(await mirror.GetStateAsync("rebuilding"));
        Assert.Null(await mirror.GetStateAsync("sync-expires"));
    }

    [Fact]
    public async Task Rebuild_ThatLosesTheLeaseDuringTheClear_StopsTheClearAndLeavesTheNewHolderAlone()
    {
        var catalog = new SlowClearCatalog(new JsonFileVfsCatalog(new InMemoryKvNode()),
            during: async (self, ct) =>
            {
                // Another instance takes the lease over part-way through, then the clear would run on.
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                await self.WriteLeaseAsync("someone-else");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            });
        var (node, mirror) = Node(catalog);
        await mirror.UpsertAsync(new VfsNodeInfo { RelativePath = VfsPath.From("stale.txt"), IsFile = true, IsDirectory = false });

        var started = DateTimeOffset.UtcNow;
        var ex = await Assert.ThrowsAsync<VfsTransientException>(() => node.RefreshAsync());

        Assert.Equal(VfsFailureReason.Timeout, ex.Reason);                             // "resumes on the next sync"
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5), "the clear was not stopped");
        Assert.Equal("someone-else", await mirror.GetStateAsync("sync-expires"));   // not renewed over, not released
        Assert.NotNull(await mirror.GetStateAsync("rebuilding"));
        Assert.NotNull(await mirror.GetAsync(VfsPath.From("stale.txt")));           // the clear never ran
    }

    private static (SharePointNode Node, NodeCatalog Mirror) Node(IVfsCatalog catalog)
    {
        var mirror  = new NodeCatalog(catalog);
        var handler = new StubHandler(Delta);
        return (new SharePointNode(new HttpClient(handler) { BaseAddress = new Uri(Base) }, "drive1", null, mirror), mirror);
    }

    // A catalog whose bulk remove - the one a clear issues - takes as long as the test says, and which
    // counts lease writes made meanwhile.
    private sealed class SlowClearCatalog(
        IContentAddressedCatalog inner, Func<SlowClearCatalog, CancellationToken, Task> during) : IContentAddressedCatalog
    {
        private const string LeasePath = ".vfs-mirror-state/sync-expires";
        private volatile bool _clearing;
        private int _leaseWrites;

        public int LeaseWritesDuringClear => _leaseWrites;

        public async ValueTask RemoveAsync(IEnumerable<VfsPath> paths, CancellationToken ct = default)
        {
            var list = paths.ToList();
            if (list.Any(p => p.ToString().StartsWith(".vfs-mirror-state", StringComparison.Ordinal)))
            {
                await inner.RemoveAsync(list, ct);   // a state key being cleared, not the wipe
                return;
            }

            _clearing = true;
            try
            {
                await during(this, ct);
                await inner.RemoveAsync(list, ct);
            }
            finally { _clearing = false; }
        }

        public async Task WriteLeaseAsync(string value)
            => await inner.PutEntryAsync(new CatalogEntry
            {
                Path = VfsPath.From(LeasePath), IsDirectory = false,
                Properties = new Dictionary<string, string?> { ["v"] = value },
            });

        public ValueTask<CatalogEntry?> PutEntryAsync(CatalogEntry entry, CancellationToken ct = default)
        {
            if (_clearing && entry.Path.ToString() == LeasePath) Interlocked.Increment(ref _leaseWrites);
            return inner.PutEntryAsync(entry, ct);
        }

        public ValueTask<CatalogEntry?> GetAsync(VfsPath p, CancellationToken ct = default) => inner.GetAsync(p, ct);
        public IAsyncEnumerable<CatalogEntry> ListChildrenAsync(VfsPath p, CancellationToken ct = default) => inner.ListChildrenAsync(p, ct);
        public ValueTask PutEntriesAsync(IEnumerable<CatalogEntry> e, CancellationToken ct = default) => inner.PutEntriesAsync(e, ct);
        public ValueTask<int> ReferenceCountAsync(string id, CancellationToken ct = default) => inner.ReferenceCountAsync(id, ct);
        public ValueTask<string?> FindContentIdByHashAsync(string h, CancellationToken ct = default) => inner.FindContentIdByHashAsync(h, ct);
        public IAsyncEnumerable<CatalogEntry> ListByContentIdAsync(string id, CancellationToken ct = default) => inner.ListByContentIdAsync(id, ct);
        public ValueTask EnsureDirectoryAsync(VfsPath p, DateTimeOffset t, CancellationToken ct = default) => inner.EnsureDirectoryAsync(p, t, ct);
        public IAsyncEnumerable<CatalogEntry> RemoveAsync(VfsPath p, CancellationToken ct = default) => inner.RemoveAsync(p, ct);
        public ValueTask MoveAsync(VfsPath f, VfsPath t, CancellationToken ct = default) => inner.MoveAsync(f, t, ct);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
