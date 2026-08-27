namespace Dytools.VirtualFileSystem;

/// <summary>
/// Why a VFS operation failed, in the library's own terms rather than the backend's.
/// The closed set a consumer can switch on without knowing whether the mount is Graph, a local
/// disk, or an object store.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is deliberately the default (zero) value: a node that hits a failure it
/// cannot map reports it as unknown-but-real with the backend exception intact, rather than
/// silently inheriting some more specific meaning it never established.
/// </remarks>
public enum VfsFailureReason
{
    /// <summary>The backend failed in a way this node could not classify. Treat as real.</summary>
    Unknown = 0,

    /// <summary>The addressed entry does not exist.</summary>
    /// <remarks>
    /// Not raised for the ordinary "is it there?" paths - <c>OpenReadAsync</c> and
    /// <c>GetInfoAsync</c> return <c>null</c> for a missing entry. This is for operations where
    /// absence is a failure, such as moving or copying from a source that is not there.
    /// </remarks>
    NotFound,

    /// <summary>The caller is not permitted to perform the operation (401 / 403 / ACL denial).</summary>
    AccessDenied,

    /// <summary>The operation collided with the existing state - a create-new over an existing
    /// entry, or a lost optimistic-concurrency race.</summary>
    Conflict,

    /// <summary>The backend is deliberately rate limiting. Always transient, and usually carries a
    /// <see cref="VfsTransientException.RetryAfter"/>.</summary>
    Throttled,

    /// <summary>The backend could not be reached, or answered that it could not service the
    /// request right now. Always transient.</summary>
    Unavailable,

    /// <summary>The operation exceeded its time budget. Always transient.</summary>
    /// <remarks>
    /// Distinct from cancellation. A caller-requested cancel is never a VFS exception at all - see
    /// <see cref="VfsFailure.ShouldWrap"/>.
    /// </remarks>
    Timeout,

    /// <summary>The path is not addressable on this backend - an illegal character, a segment the
    /// backend reserves, or a length the backend refuses.</summary>
    InvalidPath,

    /// <summary>A storage, size, or item-count limit was reached (disk full, 507, bucket quota).</summary>
    QuotaExceeded,
}

/// <summary>
/// The VFS operation that was in flight when the failure occurred. Mirrors the <see cref="IVfsNode"/>
/// surface, plus the two internal steps a node can fail during before any of those begin.
/// </summary>
public enum VfsOperation
{
    /// <summary>The operation was not recorded, or the failure happened outside one.</summary>
    Unknown = 0,

    /// <summary>Opening or consuming a read stream.</summary>
    Read,

    /// <summary>Opening, writing, or committing a write stream.</summary>
    Write,

    /// <summary>Deleting an entry.</summary>
    Delete,

    /// <summary>Copying an entry.</summary>
    Copy,

    /// <summary>Moving an entry.</summary>
    Move,

    /// <summary>Renaming an entry in place.</summary>
    Rename,

    /// <summary>Enumerating children.</summary>
    List,

    /// <summary>Testing for existence.</summary>
    Exists,

    /// <summary>Fetching metadata.</summary>
    GetInfo,

    /// <summary>Reconciling a cache or mirror against the backend (a change feed / delta run).</summary>
    Sync,

    /// <summary>Resolving the backend's own addressing before any entry operation - a SharePoint
    /// site and drive id, a bucket, a connection.</summary>
    Resolve,
}

/// <summary>
/// Which side of a node failed: the backend it fronts, or the catalog it keeps its namespace in.
/// </summary>
/// <remarks>
/// A property rather than a subclass, deliberately. Making it a type would mean a class per
/// combination of category and location (<c>VfsTransientCatalogException</c> and the rest) for a
/// distinction most callers never make. The callers that do make it - a caching node deciding
/// whether it can degrade to serving the backend directly - only need to read a bit.
/// </remarks>
public enum VfsFailureOrigin
{
    /// <summary>The storage the node fronts (Graph, the filesystem, S3, Azure).</summary>
    Backend = 0,

    /// <summary>An <see cref="Catalog.IVfsCatalog"/> call. The catalog may be the node's authority
    /// (dedupe) or only a cache (a SharePoint mirror), which is what decides whether the failure is
    /// recoverable.</summary>
    Catalog,
}
