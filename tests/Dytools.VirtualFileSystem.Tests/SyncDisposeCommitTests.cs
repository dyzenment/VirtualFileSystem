using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Dytools.VirtualFileSystem.Catalog;
using Dytools.VirtualFileSystem.Nodes.Dedupe;
using Dytools.VirtualFileSystem.Nodes.SharePoint;

namespace Dytools.VirtualFileSystem.Tests;

/// <summary>
/// The staging backends (SharePoint, S3, Azure append, dedupe) only reach the backend when the write
/// stream is closed, so a caller who writes <c>using</c> instead of <c>await using</c> takes the
/// synchronous Dispose path and blocks on an async commit. On a thread with a SynchronizationContext
/// that used to hang forever - the commit's continuations queued back onto the blocked thread.
/// These tests run that exact shape on a single-threaded pumping context, and fail by timing out.
/// </summary>
public sealed class SyncDisposeCommitTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void VfsCommit_RunSync_DoesNotDeadlock_UnderSynchronizationContext()
    {
        var ran = false;
        RunOnPumpedThread(async () =>
        {
            await Task.Yield();   // the body below now runs on the single pumped thread
            VfsCommit.RunSync(async () =>
            {
                await Task.Delay(10);   // a continuation that would post back to this blocked thread
                ran = true;
            });
        });
        Assert.True(ran);
    }

    [Fact]
    public void SyncUsing_CommitsTheWrite_UnderSynchronizationContext()
    {
        var inner   = new SlowBlobStoreNode();
        var catalog = new InMemoryVfsCatalog();
        var node    = new DedupeNode(inner, catalog);

        RunOnPumpedThread(async () =>
        {
            // The mistake this guards: `using`, not `await using`, on a commit-on-dispose stream.
            using var w = await node.OpenWriteAsync(new VfsNodeRequest(VfsPath.From("docs/a.txt")));
            await w.WriteAsync(Encoding.UTF8.GetBytes("committed from a sync dispose"));
        });

        Assert.Equal("committed from a sync dispose", ReadBack(node, "docs/a.txt"));
    }

    [Fact]
    public void AwaitUsing_CommitsTheWrite_UnderSynchronizationContext()
    {
        var inner   = new SlowBlobStoreNode();
        var catalog = new InMemoryVfsCatalog();
        var node    = new DedupeNode(inner, catalog);

        RunOnPumpedThread(async () =>
        {
            await using var w = await node.OpenWriteAsync(new VfsNodeRequest(VfsPath.From("docs/b.txt")));
            await w.WriteAsync(Encoding.UTF8.GetBytes("committed from an async dispose"));
        });

        Assert.Equal("committed from an async dispose", ReadBack(node, "docs/b.txt"));
    }

    [Fact]
    public void SharePoint_SyncUsing_UploadsTheItem_UnderSynchronizationContext()
    {
        // The reported hang: OpenWriteAsync on a SharePoint mount, `using` rather than `await using`,
        // from a thread with a context. Nothing is uploaded until the stream closes, so the whole
        // Graph round-trip runs from Dispose.
        var handler = new DelayedGraphHandler();
        var node    = new SharePointNode(
            new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") },
            "drive1");

        RunOnPumpedThread(async () =>
        {
            using var w = await node.OpenWriteAsync(
                new VfsNodeRequest(VfsPath.From("docs/report.pdf")),
                new VfsWriteOptions { Mode = VfsWriteMode.Create });
            await w.WriteAsync(Encoding.UTF8.GetBytes("%PDF-1.7 fake"));
        });

        var put = Assert.Single(handler.Requests, r => r.StartsWith("PUT ", StringComparison.Ordinal));
        Assert.Contains("drives/drive1/root:/docs/report.pdf:/content", put);
        Assert.Equal("%PDF-1.7 fake", handler.LastPutBody);
    }

    private static string? ReadBack(DedupeNode node, string path)
    {
        using var r = node.OpenReadAsync(new VfsNodeRequest(VfsPath.From(path))).GetAwaiter().GetResult();
        if (r is null) return null;
        using var sr = new StreamReader(r);
        return sr.ReadToEnd();
    }

    // -- Harness ---------------------------------------------------------------

    // Runs body() on a dedicated thread carrying a single-threaded SynchronizationContext, pumping
    // it the way a UI message loop does. Anything that blocks that thread while waiting on work
    // whose continuation is posted to the context deadlocks - which is the point.
    private static void RunOnPumpedThread(Func<Task> body)
    {
        var       context = new PumpContext();
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var task = body();
            task.ContinueWith(_ => context.Complete(), TaskScheduler.FromCurrentSynchronizationContext());
            context.Pump();
            failure = task.Exception?.GetBaseException();
        }) { IsBackground = true, Name = "vfs-sync-dispose-test" };

        thread.Start();
        Assert.True(
            thread.Join(Timeout),
            $"Timed out after {Timeout.TotalSeconds:0}s - the commit deadlocked against the "
            + "synchronization context instead of running on the thread pool.");

        if (failure is not null) throw failure;
    }

    private sealed class PumpContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public void Pump()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }

        public void Complete() => _queue.CompleteAdding();
    }

    // -- An inner node whose commit genuinely goes async ------------------------

    // The delay matters: a commit that completes synchronously never posts a continuation, so it
    // could not reproduce the deadlock this file exists to catch.
    private sealed class SlowBlobStoreNode : VfsNodeBase
    {
        private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

        public override Task<Stream?> OpenReadAsync(VfsNodeRequest req, CancellationToken ct = default)
            => Task.FromResult<Stream?>(
                _blobs.TryGetValue(Key(req), out var b) ? new MemoryStream(b, false) : null);

        public override Task<Stream> OpenWriteAsync(
            VfsNodeRequest req, VfsWriteOptions? options = null, CancellationToken ct = default)
        {
            var key = Key(req);
            return Task.FromResult<Stream>(new SlowCommitStream(bytes => _blobs[key] = bytes));
        }

        public override Task DeleteAsync(
            VfsNodeRequest req, VfsDeleteOptions? options = null, CancellationToken ct = default)
        {
            _blobs.TryRemove(Key(req), out _);
            return Task.CompletedTask;
        }

        public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest req, CancellationToken ct = default)
            => Task.FromResult<VfsNodeInfo?>(_blobs.TryGetValue(Key(req), out var b)
                ? new VfsNodeInfo { RelativePath = req.Path, IsFile = true, IsDirectory = false, SizeBytes = b.Length }
                : null);

        protected override async IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(
            VfsNodeRequest req, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        private static string Key(VfsNodeRequest req) => new(req.Path.PathSpan);

        private sealed class SlowCommitStream(Action<byte[]> commit) : MemoryStream
        {
            private bool _done;

            public override async ValueTask DisposeAsync()
            {
                await CommitAsync().ConfigureAwait(false);
                await base.DisposeAsync().ConfigureAwait(false);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) VfsCommit.RunSync(CommitAsync);
                base.Dispose(disposing);
            }

            private async Task CommitAsync()
            {
                if (_done) return;
                _done = true;
                var bytes = ToArray();
                // Deliberately NOT ConfigureAwait(false): models a backend that captures the
                // context, which is what SharePoint's commit path did. The synchronous Dispose has
                // to stay live even then, so the guarantee cannot rest on the callee's discipline.
                await Task.Delay(10);
                commit(bytes);
            }
        }
    }

    // A Graph stub that genuinely goes async, so a commit blocking the calling thread has real
    // continuations to deadlock against.
    private sealed class DelayedGraphHandler : HttpMessageHandler
    {
        public List<string> Requests   { get; } = new();
        public string?      LastPutBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri}");
            if (request.Content is not null && request.Method == HttpMethod.Put)
                LastPutBody = await request.Content.ReadAsStringAsync(ct);

            await Task.Delay(10, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"01ABC","name":"report.pdf","size":13,"file":{"mimeType":"application/pdf"}}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
