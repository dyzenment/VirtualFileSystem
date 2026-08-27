using System.Runtime.CompilerServices;

namespace Dytools.VirtualFileSystem.Tests;

/// <summary>
/// Layer 2 of the exception contract: whatever a node throws, a consumer calling
/// <see cref="IVirtualFileSystem"/> sees a <see cref="VfsException"/> - or, for a genuine cancel,
/// the <see cref="OperationCanceledException"/> untouched.
/// </summary>
public sealed class VfsPipelineExceptionTests
{
    // A node that fails every operation with whatever it was handed. Listing fails on the second
    // MoveNextAsync, not on construction, which is where a real paging failure happens.
    private sealed class FailingNode(Func<Exception> failure) : VfsNodeBase
    {
        public override Task<Stream?>      OpenReadAsync(VfsNodeRequest r, CancellationToken ct = default) => throw failure();
        public override Task<Stream>       OpenWriteAsync(VfsNodeRequest r, VfsWriteOptions? o = null, CancellationToken ct = default) => throw failure();
        public override Task               DeleteAsync(VfsNodeRequest r, VfsDeleteOptions? o = null, CancellationToken ct = default) => throw failure();
        public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest r, CancellationToken ct = default) => throw failure();

        protected override async IAsyncEnumerable<VfsNodeInfo> ListDirectoryAsync(
            VfsNodeRequest r, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new VfsNodeInfo { RelativePath = VfsPath.From("first.txt"), IsFile = true, IsDirectory = false };
            throw failure();
        }
    }

    private static IVirtualFileSystem Vfs(Func<Exception> failure)
        => VfsFactory.Build(b => b.Mount("/x", new FailingNode(failure)));

    // -- A raw backend exception never reaches the consumer --------------------

    [Fact]
    public async Task A_raw_backend_exception_becomes_a_vfs_exception()
    {
        var vfs = Vfs(() => new HttpRequestException("connection reset"));
        var ex  = await Assert.ThrowsAsync<VfsException>(() => vfs.OpenReadAsync("/x/report.pdf"));

        Assert.Equal(VfsOperation.Read, ex.Operation);
        Assert.Equal(VfsFailureReason.Unknown, ex.Reason);      // unclassified is real, not transient
        Assert.Equal("/x/report.pdf", ex.Path);
        Assert.Equal("/x", ex.Mount);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Theory]
    [InlineData(VfsOperation.Read)]
    [InlineData(VfsOperation.Write)]
    [InlineData(VfsOperation.Delete)]
    [InlineData(VfsOperation.GetInfo)]
    [InlineData(VfsOperation.Exists)]
    [InlineData(VfsOperation.Rename)]
    [InlineData(VfsOperation.List)]
    public async Task Every_operation_reports_itself(VfsOperation op)
    {
        var vfs = Vfs(() => new IOException("boom"));

        var ex = await Assert.ThrowsAsync<VfsException>(() => Invoke(vfs, op));
        Assert.Equal(op, ex.Operation);

        static async Task Invoke(IVirtualFileSystem vfs, VfsOperation op)
        {
            switch (op)
            {
                case VfsOperation.Read:    await vfs.OpenReadAsync("/x/f.txt"); break;
                case VfsOperation.Write:   await vfs.OpenWriteAsync("/x/f.txt"); break;
                case VfsOperation.Delete:  await vfs.DeleteAsync("/x/f.txt"); break;
                case VfsOperation.GetInfo: await vfs.GetInfoAsync("/x/f.txt"); break;
                case VfsOperation.Exists:  await vfs.ExistsAsync("/x/f.txt"); break;
                case VfsOperation.Rename:  await vfs.RenameAsync("/x/f.txt", "g.txt"); break;
                case VfsOperation.List:    await foreach (var _ in vfs.ListAsync("/x")) { } break;
            }
        }
    }

    // Copy reports the source: it is the entry the operation is about.
    [Fact]
    public async Task Copy_reports_the_source_path()
    {
        var vfs = VfsFactory.Build(b => b
            .Mount("/x", new FailingNode(() => new IOException("boom")))
            .Mount("/y", new Nodes.InMemory.InMemoryKvNode()));

        var ex = await Assert.ThrowsAsync<VfsException>(() => vfs.CopyAsync("/x/f.txt", "/y/f.txt"));
        Assert.Equal(VfsOperation.Copy, ex.Operation);
        Assert.Equal("/x/f.txt", ex.Path);
    }

    // -- A failure partway through a listing ------------------------------------

    [Fact]
    public async Task A_listing_that_fails_mid_enumeration_is_wrapped_after_yielding_what_it_had()
    {
        var vfs  = Vfs(() => new HttpRequestException("page 2 failed"));
        var seen = new List<string>();

        var ex = await Assert.ThrowsAsync<VfsException>(async () =>
        {
            await foreach (var entry in vfs.ListAsync("/x")) seen.Add(entry);
        });

        Assert.Equal(["/x/first.txt"], seen);                   // nothing buffered or lost
        Assert.Equal(VfsOperation.List, ex.Operation);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    // -- What the net leaves alone ----------------------------------------------

    [Fact]
    public async Task A_node_that_classified_its_own_failure_is_not_rewrapped()
    {
        var classified = new VfsTransientException(
            VfsFailureReason.Throttled, VfsOperation.Read, "/x/f.txt", "/x",
            retryAfter: TimeSpan.FromSeconds(12));

        var vfs = Vfs(() => classified);
        var ex  = await Assert.ThrowsAsync<VfsTransientException>(() => vfs.OpenReadAsync("/x/f.txt"));

        Assert.Same(classified, ex);
        Assert.Equal(TimeSpan.FromSeconds(12), ex.RetryAfter);
        Assert.Null(ex.InnerException);                         // not nested inside a second wrapper
    }

    [Fact]
    public async Task Api_misuse_from_a_node_stays_itself()
    {
        var vfs = Vfs(() => new NotSupportedException("this node cannot append"));
        await Assert.ThrowsAsync<NotSupportedException>(() => vfs.OpenWriteAsync("/x/f.txt"));
    }

    [Fact]
    public async Task A_cancel_the_caller_asked_for_stays_a_cancel()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var vfs = Vfs(() => new OperationCanceledException(cts.Token));
        var ex  = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => vfs.OpenReadAsync("/x/f.txt", cts.Token));

        Assert.IsNotType<VfsException>(ex);
    }

    // The whole reason ShouldWrap takes a token: HttpClient reports its own timeout with this exact
    // shape, and the caller's token unsignalled.
    [Fact]
    public async Task A_cancel_the_caller_did_not_ask_for_becomes_a_transient_timeout()
    {
        var vfs = Vfs(() => new TaskCanceledException("HttpClient.Timeout elapsing", new TimeoutException()));
        var ex  = await Assert.ThrowsAsync<VfsTransientException>(() => vfs.OpenReadAsync("/x/f.txt"));

        Assert.Equal(VfsFailureReason.Timeout, ex.Reason);
        Assert.Equal(VfsOperation.Read, ex.Operation);
    }

    // Mount resolution happens before any chain is entered, so it is not the net's business - a
    // path that names no mount is a configuration error, not a storage failure.
    [Fact]
    public async Task An_unmounted_path_is_still_a_configuration_error()
    {
        var vfs = Vfs(() => new IOException("boom"));
        var ex  = await Record.ExceptionAsync(() => vfs.OpenReadAsync("/nowhere/f.txt"));

        Assert.NotNull(ex);
        Assert.IsNotType<VfsException>(ex);
    }
}
