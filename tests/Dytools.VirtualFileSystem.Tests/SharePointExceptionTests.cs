using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Dytools.VirtualFileSystem.Catalog;
using Dytools.VirtualFileSystem.Nodes.InMemory;
using Dytools.VirtualFileSystem.Nodes.SharePoint;

namespace Dytools.VirtualFileSystem.Tests;

/// <summary>
/// Layer 1 of the exception contract for the SharePoint node: Graph's own failures classified where
/// they still mean something, rather than left for the pipeline net to call <c>Unknown</c>.
/// </summary>
public sealed class SharePointExceptionTests
{
    private const string Base = "https://graph.microsoft.com/v1.0/";

    // The node is built directly on the stub, so GraphHttp's retry handler is not in the way -
    // a 429 here is the one that escaped it, which is the case worth asserting on.
    private static SharePointNode Node(HttpMessageHandler handler, NodeCatalog? mirror = null)
        => new(new HttpClient(handler) { BaseAddress = new Uri(Base) }, "drive1", null, mirror);

    private static VfsNodeRequest Req(string rel = "docs/report.pdf")
        => new(VfsPath.From(rel), VfsPath.From("/sp"));

    // -- The status table -------------------------------------------------------

    [Theory]
    [InlineData(408, VfsFailureReason.Timeout)]
    [InlineData(429, VfsFailureReason.Throttled)]
    [InlineData(502, VfsFailureReason.Unavailable)]
    [InlineData(503, VfsFailureReason.Unavailable)]
    [InlineData(504, VfsFailureReason.Unavailable)]
    public async Task The_far_end_being_busy_is_transient(int status, VfsFailureReason reason)
    {
        var node = Node(new StubHandler((HttpStatusCode)status));
        var ex   = await Assert.ThrowsAsync<VfsTransientException>(() => node.GetInfoAsync(Req()));

        Assert.Equal(reason, ex.Reason);
        Assert.Equal(VfsOperation.GetInfo, ex.Operation);
    }

    [Theory]
    [InlineData(401, VfsFailureReason.AccessDenied)]
    [InlineData(403, VfsFailureReason.AccessDenied)]
    [InlineData(409, VfsFailureReason.Conflict)]
    [InlineData(507, VfsFailureReason.QuotaExceeded)]
    [InlineData(400, VfsFailureReason.Unknown)]
    public async Task A_request_the_far_end_refused_is_real(int status, VfsFailureReason reason)
    {
        var node = Node(new StubHandler((HttpStatusCode)status));
        var ex   = await Assert.ThrowsAsync<VfsException>(() => node.GetInfoAsync(Req()));

        Assert.IsNotType<VfsTransientException>(ex);
        Assert.Equal(reason, ex.Reason);
    }

    // The deliberate hole in the transient set: an opaque server error can just as easily be
    // something we sent, and retrying a bad request forever is worse than surfacing it once.
    [Fact]
    public async Task A_500_is_not_treated_as_transient()
    {
        var node = Node(new StubHandler(HttpStatusCode.InternalServerError));
        var ex   = await Assert.ThrowsAsync<VfsException>(() => node.GetInfoAsync(Req()));

        Assert.IsNotType<VfsTransientException>(ex);
        Assert.Equal(VfsFailureReason.Unknown, ex.Reason);
    }

    [Fact]
    public async Task Throttling_carries_graphs_own_retry_after()
    {
        var node = Node(new StubHandler(HttpStatusCode.TooManyRequests,
            configure: r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(37))));

        var ex = await Assert.ThrowsAsync<VfsTransientException>(() => node.GetInfoAsync(Req()));
        Assert.Equal(TimeSpan.FromSeconds(37), ex.RetryAfter);
    }

    [Fact]
    public async Task The_message_carries_the_status_and_graphs_error_code()
    {
        var node = Node(new StubHandler(HttpStatusCode.Forbidden,
            """{"error":{"code":"accessDenied","message":"Access denied to the requested resource."}}"""));

        var ex = await Assert.ThrowsAsync<VfsException>(() => node.GetInfoAsync(Req()));

        Assert.Contains("403", ex.Message);
        Assert.Contains("accessDenied", ex.Message);
        Assert.Contains("/sp/docs/report.pdf", ex.Message);
    }

    [Fact]
    public async Task A_failure_reports_the_mount_and_full_path()
    {
        var node = Node(new StubHandler(HttpStatusCode.ServiceUnavailable));
        var ex   = await Assert.ThrowsAsync<VfsTransientException>(() => node.GetInfoAsync(Req()));

        Assert.Equal("/sp", ex.Mount);
        Assert.Equal("/sp/docs/report.pdf", ex.Path);
    }

    // -- Absence is still an answer, not a failure -------------------------------

    [Fact]
    public async Task A_404_on_getinfo_is_still_null()
        => Assert.Null(await Node(new StubHandler(HttpStatusCode.NotFound)).GetInfoAsync(Req()));

    [Fact]
    public async Task A_404_on_read_is_still_null()
        => Assert.Null(await Node(new StubHandler(HttpStatusCode.NotFound)).OpenReadAsync(Req()));

    [Fact]
    public async Task A_404_on_delete_is_still_a_no_op()
        => await Node(new StubHandler(HttpStatusCode.NotFound)).DeleteAsync(Req());

    // Where absence IS a failure, it says so.
    [Fact]
    public async Task A_404_on_rename_is_a_not_found()
    {
        var node = Node(new StubHandler(HttpStatusCode.NotFound));
        var ex   = await Assert.ThrowsAsync<VfsException>(() => node.RenameAsync(Req(), "new.pdf"));

        Assert.Equal(VfsFailureReason.NotFound, ex.Reason);
        Assert.Equal(VfsOperation.Rename, ex.Operation);
    }

    // -- Failures with no response at all ----------------------------------------

    [Fact]
    public async Task A_connection_that_never_answered_is_transient()
    {
        var node = Node(new ThrowingHandler(() =>
            new HttpRequestException("No such host is known.", new SocketException(11001))));

        var ex = await Assert.ThrowsAsync<VfsTransientException>(() => node.GetInfoAsync(Req()));
        Assert.Equal(VfsFailureReason.Unavailable, ex.Reason);
    }

    [Fact]
    public async Task An_httpclient_timeout_is_a_transient_timeout_not_a_cancel()
    {
        var node = Node(new ThrowingHandler(() =>
            new TaskCanceledException("HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException())));

        var ex = await Assert.ThrowsAsync<VfsTransientException>(() => node.GetInfoAsync(Req()));
        Assert.Equal(VfsFailureReason.Timeout, ex.Reason);
    }

    [Fact]
    public async Task A_cancel_the_caller_asked_for_stays_a_cancel()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var node = Node(new ThrowingHandler(() => new OperationCanceledException(cts.Token)));
        var ex   = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.GetInfoAsync(Req(), cts.Token));

        Assert.IsNotType<VfsException>(ex);
    }

    // A status another helper already turned into an HttpRequestException must still be classified
    // by that status - listing pages go through GetFromJsonAsync, so this is the paging path.
    [Fact]
    public async Task A_status_wrapped_by_a_json_helper_is_classified_by_the_status()
    {
        var node = Node(new StubHandler(HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<VfsTransientException>(async () =>
        {
            await foreach (var _ in node.ListAsync(Req("docs"), VfsListOptions.Default)) { }
        });

        Assert.Equal(VfsFailureReason.Throttled, ex.Reason);
        Assert.Equal(VfsOperation.List, ex.Operation);
    }

    // -- Writes ------------------------------------------------------------------

    [Fact]
    public async Task Create_new_over_an_existing_item_is_a_conflict()
    {
        var node   = Node(new StubHandler(HttpStatusCode.Conflict));
        var stream = await node.OpenWriteAsync(Req(), new VfsWriteOptions { Mode = VfsWriteMode.CreateNew });
        await stream.WriteAsync("hello"u8.ToArray());

        // The upload runs on close, outside every method the pipeline guards.
        var ex = await Assert.ThrowsAsync<VfsException>(async () => await stream.DisposeAsync());

        Assert.Equal(VfsFailureReason.Conflict, ex.Reason);
        Assert.Equal(VfsOperation.Write, ex.Operation);
        Assert.Equal("/sp/docs/report.pdf", ex.Path);
    }

    [Fact]
    public async Task An_upload_that_is_throttled_is_transient()
    {
        var node   = Node(new StubHandler(HttpStatusCode.ServiceUnavailable));
        var stream = await node.OpenWriteAsync(Req());
        await stream.WriteAsync("hello"u8.ToArray());

        var ex = await Assert.ThrowsAsync<VfsTransientException>(async () => await stream.DisposeAsync());
        Assert.Equal(VfsFailureReason.Unavailable, ex.Reason);
    }

    // -- The catalog side of a caching mount -------------------------------------

    // A caching mount has two things that can fail, and they are not the same failure: the drive
    // being unreachable is not the mirror being unreachable.
    [Fact]
    public async Task A_mirror_failure_is_reported_as_a_catalog_failure()
    {
        var node = Node(new StubHandler(HttpStatusCode.NotFound), new NodeCatalog(new FailingCatalog()));

        // A 404 read asks the mirror to drop its stale row; that is the call that fails here.
        var ex = await Assert.ThrowsAsync<VfsException>(() => node.OpenReadAsync(Req()));

        Assert.Equal(VfsFailureOrigin.Catalog, ex.Origin);
        Assert.Equal(VfsOperation.Read, ex.Operation);
        Assert.Contains("VFS catalog", ex.Message);
    }

    [Fact]
    public async Task A_graph_failure_on_a_caching_mount_is_still_a_backend_failure()
    {
        var node = Node(new StubHandler(HttpStatusCode.ServiceUnavailable), new NodeCatalog(new FailingCatalog()));
        var ex   = await Assert.ThrowsAsync<VfsTransientException>(() => node.GetInfoAsync(Req()));

        Assert.Equal(VfsFailureOrigin.Backend, ex.Origin);
    }

    // -- Stubs --------------------------------------------------------------------

    private sealed class StubHandler(
        HttpStatusCode status, string? body = null, Action<HttpResponseMessage>? configure = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(status);
            if (body is not null) resp.Content = new StringContent(body, Encoding.UTF8, "application/json");
            configure?.Invoke(resp);
            return Task.FromResult(resp);
        }
    }

    private sealed class ThrowingHandler(Func<Exception> failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw failure();
    }

    // Fails every namespace call the way a database that is down would.
    private sealed class FailingCatalog : IVfsCatalog, IContentAddressedCatalog
    {
        private static Exception Down() => new InvalidOperationException("the catalog store is unreachable");

        public ValueTask<CatalogEntry?> GetAsync(VfsPath p, CancellationToken ct = default) => throw Down();
        public IAsyncEnumerable<CatalogEntry> ListChildrenAsync(VfsPath p, CancellationToken ct = default) => throw Down();
        public ValueTask<CatalogEntry?> PutEntryAsync(CatalogEntry e, CancellationToken ct = default) => throw Down();
        public ValueTask EnsureDirectoryAsync(VfsPath p, DateTimeOffset ts, CancellationToken ct = default) => throw Down();
        public IAsyncEnumerable<CatalogEntry> RemoveAsync(VfsPath p, CancellationToken ct = default) => throw Down();
        public ValueTask MoveAsync(VfsPath from, VfsPath to, CancellationToken ct = default) => throw Down();
        public ValueTask<int> ReferenceCountAsync(string contentId, CancellationToken ct = default) => throw Down();
        public ValueTask<string?> FindContentIdByHashAsync(string hash, CancellationToken ct = default) => throw Down();
    }
}
