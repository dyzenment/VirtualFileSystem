namespace Dytools.VirtualFileSystem;

/// <summary>
/// Builds <see cref="VfsException"/>s, and decides what is even eligible to become one.
/// </summary>
/// <remarks>
/// The canonical boundary, used by nodes and by the pipeline alike. The filter does the deciding so
/// that anything passing through is never caught in the first place - no <c>throw;</c>, no stack
/// rewriting, no accidental swallowing:
/// <code>
/// try { /* backend call */ }
/// catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct))
/// {
///     throw VfsFailure.Wrap(ex, VfsOperation.Read, request);
/// }
/// </code>
/// A node that <em>can</em> classify its backend should do so at the call site and throw directly
/// through <see cref="Create"/> / <see cref="Transient"/>; <see cref="Wrap(Exception, VfsOperation, in VfsNodeRequest, VfsFailureOrigin)"/> is the fallback for
/// whatever it did not recognise.
/// </remarks>
public static class VfsFailure
{
    /// <summary>
    /// Whether <paramref name="ex"/> should be turned into a <see cref="VfsException"/>. Intended
    /// for an exception filter, so that everything else is left uncaught rather than rethrown.
    /// </summary>
    /// <param name="ex">The exception that escaped the backend call.</param>
    /// <param name="ct">The token the operation was given. Not optional - it is what separates a
    /// cancel from a timeout.</param>
    /// <remarks>
    /// Four things pass through untouched:
    /// <list type="bullet">
    /// <item><description>An existing <see cref="VfsException"/> - already in VFS terms, and
    /// re-wrapping it would bury the reason a node worked out.</description></item>
    /// <item><description><b>Genuine cancellation.</b> Wrapping it breaks every
    /// <c>catch (OperationCanceledException)</c> in the ecosystem and the cooperative-cancellation
    /// contract with it.</description></item>
    /// <item><description>Exceptions that report a misuse of this library's own API -
    /// <see cref="ArgumentException"/>, <see cref="NotSupportedException"/>,
    /// <see cref="NotImplementedException"/>, <see cref="ObjectDisposedException"/>. They say the
    /// caller called wrong, not that storage failed, and dressing them as storage failures would
    /// hide bugs behind a retry.</description></item>
    /// <item><description><see cref="OutOfMemoryException"/>, which nothing should be catching.</description></item>
    /// </list>
    /// <para>
    /// <b>Cancellation is decided by the token, not the type.</b> <see cref="HttpClient"/> reports
    /// its own timeout as a <see cref="TaskCanceledException"/> with the caller's token never
    /// signalled - so a type check alone files every backend timeout under "the caller cancelled",
    /// which is the one category a consumer is most likely to treat as expected and never alert on.
    /// A cancellation whose token was not the caller's is therefore a failure, and becomes
    /// <see cref="VfsFailureReason.Timeout"/>.
    /// </para>
    /// </remarks>
    public static bool ShouldWrap(Exception ex, CancellationToken ct)
    {
        if (ex is VfsException) return false;

        // The caller asked to stop: pass it through. Otherwise something else cancelled the call
        // out from under us - an HttpClient timeout, an internal budget - and that is a failure.
        if (ex is OperationCanceledException) return !ct.IsCancellationRequested;

        // ObjectDisposedException is listed explicitly because it derives from
        // InvalidOperationException, which IS wrapped - a backend client that throws one is
        // reporting a broken connection, not an API misuse.
        return ex is not (ArgumentException
                       or NotSupportedException
                       or NotImplementedException
                       or ObjectDisposedException
                       or OutOfMemoryException);
    }

    /// <summary>
    /// Turns a backend exception this node could not classify into a <see cref="VfsException"/>,
    /// preserving it as the inner exception. Gate the call with <see cref="ShouldWrap"/>.
    /// </summary>
    /// <remarks>
    /// Classifies only what is identifiable without knowing the backend: a timeout. Everything else
    /// becomes <see cref="VfsFailureReason.Unknown"/> and therefore real, which is the failure
    /// direction that surfaces rather than the one that silently retries.
    /// </remarks>
    public static VfsException Wrap(
        Exception        ex,
        VfsOperation     operation,
        string?          path   = null,
        string?          mount  = null,
        VfsFailureOrigin origin = VfsFailureOrigin.Backend)
    {
        if (ex is VfsException already) return already;

        // Reaching here through ShouldWrap, a cancellation is one the caller did not ask for.
        if (ex is TimeoutException or OperationCanceledException)
            return new VfsTransientException(
                VfsFailureReason.Timeout, operation, path, mount, innerException: ex, origin: origin);

        return new VfsException(
            VfsFailureReason.Unknown, operation, path, mount, innerException: ex, origin: origin);
    }

    /// <inheritdoc cref="Wrap(Exception, VfsOperation, string?, string?, VfsFailureOrigin)"/>
    /// <param name="ex">The exception that escaped the backend call.</param>
    /// <param name="operation">The operation that was in flight.</param>
    /// <param name="request">The request being serviced; supplies the mount and full path.</param>
    /// <param name="origin">Whether the backend or the catalog failed.</param>
    public static VfsException Wrap(
        Exception ex, VfsOperation operation, in VfsNodeRequest request,
        VfsFailureOrigin origin = VfsFailureOrigin.Backend)
        => Wrap(ex, operation, FullPath(request), MountOf(request), origin);

    /// <summary>
    /// A real failure a node recognised itself - the request will fail the same way if repeated.
    /// </summary>
    /// <param name="reason">What went wrong, in VFS terms.</param>
    /// <param name="operation">The operation that was in flight.</param>
    /// <param name="request">The request being serviced; supplies the mount and full path.</param>
    /// <param name="message">Overrides the generated message.</param>
    /// <param name="innerException">The backend's own exception, when there is one.</param>
    /// <param name="origin">Whether the backend or the catalog failed.</param>
    public static VfsException Create(
        VfsFailureReason reason,
        VfsOperation     operation,
        in VfsNodeRequest request,
        string?          message        = null,
        Exception?       innerException = null,
        VfsFailureOrigin origin         = VfsFailureOrigin.Backend)
        => new(reason, operation, FullPath(request), MountOf(request), message, innerException, origin);

    /// <summary>
    /// A failure a node recognised as the far end being unable to service the request. Retrying the
    /// same operation later is reasonable.
    /// </summary>
    /// <param name="reason">Usually <see cref="VfsFailureReason.Throttled"/>,
    /// <see cref="VfsFailureReason.Unavailable"/> or <see cref="VfsFailureReason.Timeout"/>.</param>
    /// <param name="operation">The operation that was in flight.</param>
    /// <param name="request">The request being serviced; supplies the mount and full path.</param>
    /// <param name="retryAfter">How long the backend asked the caller to wait, when it said so.</param>
    /// <param name="message">Overrides the generated message.</param>
    /// <param name="innerException">The backend's own exception, when there is one.</param>
    /// <param name="origin">Whether the backend or the catalog failed.</param>
    public static VfsTransientException Transient(
        VfsFailureReason reason,
        VfsOperation     operation,
        in VfsNodeRequest request,
        TimeSpan?        retryAfter     = null,
        string?          message        = null,
        Exception?       innerException = null,
        VfsFailureOrigin origin         = VfsFailureOrigin.Backend)
        => new(reason, operation, FullPath(request), MountOf(request), message, innerException, origin, retryAfter);

    /// <summary>
    /// The request's mount-qualified path as a string, or null when there is nothing to report.
    /// Error paths only - it materialises what <see cref="VfsNodeRequest"/> deliberately keeps as
    /// a slice.
    /// </summary>
    /// <param name="request">The request being serviced.</param>
    public static string? FullPath(in VfsNodeRequest request)
        => VfsPath.From(request.Mount, request.Path).ToString() is { Length: > 0 } s ? s : null;

    /// <summary>The request's mount prefix as a string, or null when it was built outside the pipeline.</summary>
    /// <param name="request">The request being serviced.</param>
    public static string? MountOf(in VfsNodeRequest request)
        => request.Mount.ToString() is { Length: > 0 } s ? s : null;
}
