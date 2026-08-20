namespace Dytools.VirtualFileSystem;

/// <summary>
/// Marker interface. Nodes implement this to declare that their <c>GetInfoAsync</c>
/// may return <c>VfsPropertyKeys.SymlinkTarget</c> in <c>VfsNodeInfo.Properties</c>.
/// <para>
/// <c>SymlinkMiddleware</c> checks for this capability before calling <c>GetInfoAsync</c>.
/// Nodes that do NOT implement it are skipped entirely - zero overhead per read.
/// </para>
/// <para>
/// If you need to enable symlink checking on a third-party node you cannot modify,
/// pass its <see cref="System.Type"/> to <c>IVfsBuilder.UseSymlinks(typeof(ThirdPartyNode))</c>.
/// </para>
/// </summary>
public interface ISymlinkCapableNode { }

/// <summary>
/// Maps VFS paths to stable content identifiers (e.g. SHA-256 hash) and
/// maintains a reference count per content ID. When refcount reaches zero,
/// the node may safely delete the backing bytes from storage.
/// <para>
/// This is a contract only - implementations live in node packages.
/// Example: an S3 hard-link store keeps path→contentId in DynamoDB or <c>hardlinks.json</c> on S3;
/// an in-memory hard-link store keeps path→contentId in a <c>ConcurrentDictionary</c>.
/// </para>
/// <para>Thread-safety: implementations must be thread-safe.</para>
/// </summary>
public interface IHardLinkStore
{
    /// <summary>Returns the content ID for this path, or <c>null</c> if not tracked.</summary>
    ValueTask<string?> ResolveContentIdAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Records that <paramref name="relativePath"/> now points to <paramref name="contentId"/>.</summary>
    /// <returns>The new reference count.</returns>
    ValueTask<int> AddReferenceAsync(string contentId, string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Removes the path record and decrements refcount. If refcount reaches 0, the caller is
    /// responsible for deleting the backing bytes.
    /// </summary>
    /// <returns>The new reference count.</returns>
    ValueTask<int> ReleaseReferenceAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Returns the current refcount for a content ID (0 if unknown).</summary>
    ValueTask<int> GetRefCountAsync(string contentId, CancellationToken ct = default);

    /// <summary>Enumerates all paths currently pointing to this content ID.</summary>
    IAsyncEnumerable<string> GetLinksAsync(string contentId, CancellationToken ct = default);
}

/// <summary>
/// Implemented by nodes that perform content-addressed storage with reference
/// counting. Exposed via <c>VfsNodeBase.GetCapability&lt;IDeduplicatingNode&gt;()</c>.
/// <para>
/// VFS Core never calls this - it is a consumer-facing escape hatch.
/// The node handles deduplication transparently inside <c>OpenWriteAsync</c> / <c>DeleteAsync</c>.
/// </para>
/// </summary>
/// <remarks>
/// Usage:
/// <code>
///   var dedup = vfs.GetCapability&lt;IDeduplicatingNode&gt;("/s3/attachments/logo.png");
///   if (dedup is not null)
///   {
///       var id    = await dedup.HardLinks.ResolveContentIdAsync("attachments/logo.png");
///       var count = await dedup.HardLinks.GetRefCountAsync(id!);
///       // "logo.png is referenced by {count} paths"
///   }
/// </code>
/// </remarks>
public interface IDeduplicatingNode
{
    /// <summary>The hard-link store backing this node's content-addressed storage.</summary>
    IHardLinkStore HardLinks { get; }
}
