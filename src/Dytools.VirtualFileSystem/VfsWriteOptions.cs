namespace Dytools.VirtualFileSystem;

/// <summary>
/// Options for a write. Passed through the pipeline (middleware can read or rewrite it) to the node,
/// mirroring how <see cref="VfsListOptions"/> works for listings.
///
/// null options anywhere means <see cref="Default"/>: create-or-truncate, timestamps left to the backend.
/// </summary>
/// <remarks>
/// Timestamps ride on the write rather than being a separate operation because that is the only shape
/// every backend can honour. S3 object metadata is immutable once written, so a later set means a
/// server-side copy of the whole object; Azure and SharePoint can carry the value in the same request
/// as the upload but need a second round-trip afterwards. Asking for it up front costs nothing
/// anywhere, which a <c>SetTimesAsync</c> could not have said.
///
/// A node honours what its backend supports and ignores the rest - check
/// <see cref="VfsEntryInfo.ModifiedAt"/> after the write if it has to be exact.
/// </remarks>
public sealed record VfsWriteOptions
{
    /// <summary>The defaults: <see cref="VfsWriteMode.Create"/>, no timestamps requested.</summary>
    public static readonly VfsWriteOptions Default = new();

    /// <summary>How existing content at the path is treated.</summary>
    public VfsWriteMode Mode { get; init; } = VfsWriteMode.Create;

    /// <summary>
    /// Last-modified time to stamp on the written entry, or null to let the backend set its own.
    /// <para>
    /// Honoured by: LocalFs (all platforms), SharePoint (<c>fileSystemInfo</c>), Azure and S3 (custom
    /// object metadata, since their own Last-Modified is service-controlled), and catalog-backed
    /// nodes such as dedupe, which store it as a field and so record it exactly. Ignored by
    /// in-memory, which has no timestamp at all.
    /// <para>
    /// One caveat on S3: a listing cannot return object metadata, so a value set here shows up in
    /// <c>GetInfoAsync</c> and in a mirrored listing, but a direct unmirrored listing reports the
    /// service time.
    /// </para>
    /// </para>
    /// </summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>
    /// Creation time to stamp on the written entry, or null to let the backend set its own.
    /// Honoured on Windows and macOS; Linux has no settable birth time, so it is ignored there.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// Lets a bare <see cref="VfsWriteMode"/> stand in wherever options are accepted, so
    /// <c>OpenWriteAsync(path, VfsWriteMode.Append)</c> keeps reading the way it always has.
    /// </summary>
    public static implicit operator VfsWriteOptions(VfsWriteMode mode) => new() { Mode = mode };
}
