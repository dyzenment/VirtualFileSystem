namespace Dytools.VirtualFileSystem;

/// <summary>
/// The failure type every VFS operation reports, whatever mount it ran against.
/// </summary>
/// <remarks>
/// Modelled on Entity Framework's <c>DbUpdateException</c>: <b>wrap, don't hide</b>. The common
/// case is backend-agnostic - <see cref="Reason"/>, <see cref="Operation"/> and
/// <see cref="Path"/> are stated in the library's own terms, so a consumer can write one code path
/// for <c>/sp/docs</c> and <c>/local/c</c>. The rare case that genuinely needs the raw error can
/// still reach it: <see cref="Exception.InnerException"/> is always the backend's own exception,
/// unaltered.
/// <para>
/// Two categories, not three. <see cref="VfsTransientException"/> means the far end could not
/// service the request; this base type means the request itself failed and retrying it unchanged
/// will fail again. Transient is the only one a node has to positively recognise, so anything a
/// node cannot classify surfaces as real (with <see cref="VfsFailureReason.Unknown"/>) rather than
/// being quietly tolerated.
/// </para>
/// <para>
/// Cancellation is not either category. A caller-requested cancel stays an
/// <see cref="OperationCanceledException"/> and never becomes a <see cref="VfsException"/> - see
/// <see cref="VfsFailure.ShouldWrap"/> for why a backend timeout is not the same thing, despite
/// arriving as the same type.
/// </para>
/// <para>
/// Consumers catching both categories must order the arms with
/// <see cref="VfsTransientException"/> first; it derives from this type, so a leading
/// <c>catch (VfsException)</c> collapses the two.
/// </para>
/// </remarks>
public class VfsException : Exception
{
    /// <summary>Creates a VFS failure. Every facet but <paramref name="reason"/> is optional, so
    /// call it with named arguments for whatever the throw site actually knows.</summary>
    /// <param name="reason">What went wrong, in VFS terms.</param>
    /// <param name="operation">The operation that was in flight.</param>
    /// <param name="path">The full VFS path involved, where there is one.</param>
    /// <param name="mount">The mount prefix the failure came from.</param>
    /// <param name="message">Overrides the generated message. Leave null for the standard wording.</param>
    /// <param name="innerException">The backend's own exception. Preserve it whenever there is one.</param>
    /// <param name="origin">Whether the backend or the catalog failed.</param>
    public VfsException(
        VfsFailureReason  reason,
        VfsOperation      operation      = VfsOperation.Unknown,
        string?           path           = null,
        string?           mount          = null,
        string?           message        = null,
        Exception?        innerException = null,
        VfsFailureOrigin  origin         = VfsFailureOrigin.Backend)
        : base(message ?? BuildMessage(reason, operation, path, origin, innerException), innerException)
    {
        Reason    = reason;
        Operation = operation;
        Path      = path;
        Mount     = mount;
        Origin    = origin;
    }

    /// <summary>What went wrong, in VFS terms.</summary>
    public VfsFailureReason Reason { get; }

    /// <summary>The operation that was in flight.</summary>
    public VfsOperation Operation { get; }

    /// <summary>
    /// The full VFS path involved (mount prefix included), or null when the failure was not about
    /// one entry - resolving a site, or a bulk catalog write.
    /// </summary>
    /// <remarks>
    /// A materialised string rather than a <see cref="VfsPath"/>: an exception outlives the call
    /// that made it and is logged and serialised, so it should not hold a view over a buffer it did
    /// not allocate.
    /// </remarks>
    public string? Path { get; }

    /// <summary>The mount prefix the failure came from (e.g. <c>/sp/docs</c>), or null when unknown.</summary>
    public string? Mount { get; }

    /// <summary>Whether the node's backend or its catalog was the thing that failed.</summary>
    public VfsFailureOrigin Origin { get; }

    private static string BuildMessage(
        VfsFailureReason reason, VfsOperation operation, string? path, VfsFailureOrigin origin, Exception? inner)
    {
        var what   = origin == VfsFailureOrigin.Catalog ? "VFS catalog" : "VFS";
        var where  = string.IsNullOrEmpty(path) ? "" : $" on '{path}'";
        var detail = inner is null ? "" : $" See the inner exception ({inner.GetType().Name}) for details.";
        return $"{what} {operation} failed{where} ({reason}).{detail}";
    }
}

/// <summary>
/// The far end could not service the request, as opposed to the request being wrong. Throttling, an
/// unreachable host, a 503, a timeout: retrying the same operation later is reasonable.
/// </summary>
/// <remarks>
/// This is the one category a node must positively identify. Everything it cannot recognise stays a
/// plain <see cref="VfsException"/>, which is the safe direction - an unrecognised failure gets
/// treated as real and surfaces, rather than being retried forever or silently absorbed.
/// </remarks>
public class VfsTransientException : VfsException
{
    /// <summary>Creates a transient VFS failure.</summary>
    /// <param name="reason">Usually <see cref="VfsFailureReason.Throttled"/>,
    /// <see cref="VfsFailureReason.Unavailable"/> or <see cref="VfsFailureReason.Timeout"/>.</param>
    /// <param name="operation">The operation that was in flight.</param>
    /// <param name="path">The full VFS path involved, where there is one.</param>
    /// <param name="mount">The mount prefix the failure came from.</param>
    /// <param name="message">Overrides the generated message. Leave null for the standard wording.</param>
    /// <param name="innerException">The backend's own exception. Preserve it whenever there is one.</param>
    /// <param name="origin">Whether the backend or the catalog failed.</param>
    /// <param name="retryAfter">How long the backend asked the caller to wait, when it said so.</param>
    public VfsTransientException(
        VfsFailureReason  reason,
        VfsOperation      operation      = VfsOperation.Unknown,
        string?           path           = null,
        string?           mount          = null,
        string?           message        = null,
        Exception?        innerException = null,
        VfsFailureOrigin  origin         = VfsFailureOrigin.Backend,
        TimeSpan?         retryAfter     = null)
        : base(reason, operation, path, mount, message, innerException, origin)
        => RetryAfter = retryAfter;

    /// <summary>
    /// How long the backend asked the caller to wait before retrying, or null when it did not say.
    /// </summary>
    /// <remarks>
    /// Graph sends this with throttling responses, and ignoring it is how throttling becomes more
    /// throttling. A caller that backs off on its own schedule should still take this as a floor.
    /// </remarks>
    public TimeSpan? RetryAfter { get; }
}
