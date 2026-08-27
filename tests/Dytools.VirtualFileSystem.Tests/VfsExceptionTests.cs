using System.Net.Sockets;

namespace Dytools.VirtualFileSystem.Tests;

/// <summary>
/// The exception contract itself: what is eligible to become a <see cref="VfsException"/>, what it
/// carries, and the three categories a consumer is meant to be able to tell apart.
/// </summary>
public sealed class VfsExceptionTests
{
    private static VfsNodeRequest Request(string mount = "/sp/docs", string rel = "folder/report.pdf")
        => new(VfsPath.From(rel), VfsPath.From(mount));

    // -- ShouldWrap: cancellation ---------------------------------------------

    [Fact]
    public void Cancellation_the_caller_asked_for_passes_through()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(VfsFailure.ShouldWrap(new OperationCanceledException(cts.Token), cts.Token));
        Assert.False(VfsFailure.ShouldWrap(new TaskCanceledException(), cts.Token));
    }

    // The one that matters: HttpClient reports its own timeout as a TaskCanceledException with the
    // caller's token unsignalled. Typed as cancellation, it is really an outage.
    [Fact]
    public void Cancellation_the_caller_did_not_ask_for_is_a_transient_timeout()
    {
        var httpTimeout = new TaskCanceledException("The request was canceled due to the configured "
            + "HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException());

        Assert.True(VfsFailure.ShouldWrap(httpTimeout, CancellationToken.None));

        var wrapped = VfsFailure.Wrap(httpTimeout, VfsOperation.Read, Request());
        var transient = Assert.IsType<VfsTransientException>(wrapped);
        Assert.Equal(VfsFailureReason.Timeout, transient.Reason);
        Assert.Same(httpTimeout, transient.InnerException);
    }

    // -- ShouldWrap: what is out of scope --------------------------------------

    [Fact]
    public void An_existing_vfs_exception_is_never_rewrapped()
    {
        var already = new VfsException(VfsFailureReason.AccessDenied, VfsOperation.Write);
        Assert.False(VfsFailure.ShouldWrap(already, CancellationToken.None));
        Assert.Same(already, VfsFailure.Wrap(already, VfsOperation.Read, Request()));
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(ArgumentNullException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(NotImplementedException))]
    [InlineData(typeof(ObjectDisposedException))]
    public void Api_misuse_is_not_dressed_up_as_a_storage_failure(Type type)
    {
        var ex = type == typeof(ObjectDisposedException)
            ? new ObjectDisposedException("node")
            : (Exception)Activator.CreateInstance(type)!;
        Assert.False(VfsFailure.ShouldWrap(ex, CancellationToken.None));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void Backend_failures_are_wrapped(Type type)
        => Assert.True(VfsFailure.ShouldWrap((Exception)Activator.CreateInstance(type)!, CancellationToken.None));

    // A broken socket is not an API misuse even though ObjectDisposedException sits under
    // InvalidOperationException - the whole reason that exclusion is spelled out by exact type.
    [Fact]
    public void A_transport_failure_under_invalid_operation_is_still_wrapped()
        => Assert.True(VfsFailure.ShouldWrap(new SocketException(10054), CancellationToken.None));

    // -- Wrap: the default is real, not transient ------------------------------

    [Fact]
    public void An_unclassified_failure_surfaces_as_real_and_unknown()
    {
        var inner   = new HttpRequestException("boom");
        var wrapped = VfsFailure.Wrap(inner, VfsOperation.GetInfo, Request());

        Assert.IsType<VfsException>(wrapped);              // exactly the base - not transient
        Assert.Equal(VfsFailureReason.Unknown, wrapped.Reason);
        Assert.Equal(VfsOperation.GetInfo, wrapped.Operation);
        Assert.Same(inner, wrapped.InnerException);
    }

    [Fact]
    public void A_wrapped_failure_reports_where_it_happened()
    {
        var wrapped = VfsFailure.Wrap(new IOException("boom"), VfsOperation.Delete, Request());

        Assert.Equal("/sp/docs", wrapped.Mount);
        Assert.Equal("/sp/docs/folder/report.pdf", wrapped.Path);
        Assert.Equal(VfsFailureOrigin.Backend, wrapped.Origin);
    }

    // Outside the pipeline (a unit test, a node driven directly) there is no mount to report. The
    // path still renders, rooted by VfsPath's own empty-base rule rather than by a real mount.
    [Fact]
    public void A_request_built_outside_the_pipeline_reports_no_mount()
    {
        var wrapped = VfsFailure.Wrap(new IOException(), VfsOperation.Read, new VfsNodeRequest(VfsPath.From("a.txt")));
        Assert.Null(wrapped.Mount);
        Assert.Equal("/a.txt", wrapped.Path);
    }

    // -- Node-authored failures ------------------------------------------------

    [Fact]
    public void A_node_can_state_a_reason_it_recognised()
    {
        var ex = VfsFailure.Create(VfsFailureReason.Conflict, VfsOperation.Write, Request());
        Assert.Equal(VfsFailureReason.Conflict, ex.Reason);
        Assert.IsType<VfsException>(ex);
    }

    [Fact]
    public void Throttling_carries_the_backends_own_retry_delay()
    {
        var ex = VfsFailure.Transient(
            VfsFailureReason.Throttled, VfsOperation.List, Request(), retryAfter: TimeSpan.FromSeconds(23));

        Assert.Equal(TimeSpan.FromSeconds(23), ex.RetryAfter);
        Assert.Equal(VfsFailureReason.Throttled, ex.Reason);
    }

    [Fact]
    public void A_catalog_failure_says_so()
    {
        var ex = VfsFailure.Wrap(
            new IOException("db down"), VfsOperation.Sync, Request(), VfsFailureOrigin.Catalog);

        Assert.Equal(VfsFailureOrigin.Catalog, ex.Origin);
        Assert.Contains("VFS catalog", ex.Message);
    }

    // -- The message ------------------------------------------------------------

    [Fact]
    public void The_message_names_the_operation_path_and_reason()
    {
        var ex = VfsFailure.Create(
            VfsFailureReason.AccessDenied, VfsOperation.Read, Request(), innerException: new IOException());

        Assert.Equal(
            "VFS Read failed on '/sp/docs/folder/report.pdf' (AccessDenied). "
            + "See the inner exception (IOException) for details.",
            ex.Message);
    }

    [Fact]
    public void The_message_omits_the_path_when_there_is_none()
        => Assert.Equal(
            "VFS Resolve failed (Unavailable).",
            new VfsException(VfsFailureReason.Unavailable, VfsOperation.Resolve).Message);

    // -- The three categories a consumer switches on ---------------------------

    [Fact]
    public void Transient_real_and_cancelled_are_separable_in_that_order()
    {
        Assert.Equal("transient", Categorise(new VfsTransientException(VfsFailureReason.Throttled)));
        Assert.Equal("real",      Categorise(new VfsException(VfsFailureReason.AccessDenied)));
        Assert.Equal("cancelled", Categorise(new OperationCanceledException()));

        static string Categorise(Exception ex)
        {
            try { throw ex; }
            catch (OperationCanceledException) { return "cancelled"; }
            catch (VfsTransientException)      { return "transient"; }
            catch (VfsException)               { return "real"; }
        }
    }

    [Fact]
    public void Unspecified_facets_default_to_unknown_rather_than_something_specific()
    {
        var ex = new VfsException(VfsFailureReason.Unknown);
        Assert.Equal(default, ex.Reason);
        Assert.Equal(default, ex.Operation);
        Assert.Equal(VfsFailureOrigin.Backend, ex.Origin);
    }
}
