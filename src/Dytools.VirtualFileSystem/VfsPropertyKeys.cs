namespace Dytools.VirtualFileSystem;

/// <summary>
/// Constants for <see cref="VfsNodeInfo"/>.Properties and <see cref="VfsEntryInfo"/>.Properties keys.
/// Nodes use these when surfacing optional metadata that has no place in the
/// cross-platform core fields. Consumers use them for safe key access.
/// </summary>
public static class VfsPropertyKeys
{
    /// <summary>
    /// Set by LocalFsNode when the OS reports a reparse point or Unix symlink.
    /// Value: bool (true).
    /// </summary>
    public const string PhysicalSymlink = "PhysicalSymlink";

    /// <summary>
    /// Set by nodes that support node-level symlinks.
    /// Value: string - the absolute VFS path this entry points to.
    /// <see cref="Middleware.SymlinkMiddleware"/> reads this key to follow the pointer.
    /// </summary>
    public const string SymlinkTarget = "SymlinkTarget";

    /// <summary>
    /// Set by deduplicating nodes (IDeduplicatingNode) on GetInfo operations.
    /// Value: string - stable content identifier (e.g. lowercase hex SHA-256).
    /// </summary>
    public const string ContentId = "ContentId";

    /// <summary>
    /// Parking slot for a stream opened by a node's GetInfoAsync during symlink detection.
    /// Value: Stream - seekable, seeked back to position 0.
    /// </summary>
    /// <remarks>
    /// Lifecycle:
    /// <list type="bullet">
    ///   <item>GetInfoAsync - node opens stream to check magic header, stores it here if NOT a symlink.
    ///         Node must NOT dispose the stream after storing it.</item>
    ///   <item>FollowAsync - if a symlink IS found and the context is rerouted,
    ///         <see cref="Middleware.SymlinkMiddleware"/> disposes the cached stream
    ///         (it belongs to the pointer file, not the target).</item>
    ///   <item>OpenReadAsync - node checks CallContext for its own cached stream and returns it directly,
    ///         skipping a second open. Node removes the entry and owns disposal from here.</item>
    /// </list>
    /// Usage in GetInfoAsync (node that implements ISymlinkCapableNode):
    /// <code>
    ///   if (!isSymlink &amp;&amp; stream.CanSeek) {
    ///       stream.Seek(0, SeekOrigin.Begin);
    ///       request.CallContext?[VfsPropertyKeys.CachedReadStream] = stream;
    ///       // do NOT dispose - node will reclaim it in OpenReadAsync
    ///   }
    /// </code>
    /// Usage in OpenReadAsync (same node):
    /// <code>
    ///   if (request.CallContext?.TryGetValue(VfsPropertyKeys.CachedReadStream, out var cached) == true
    ///       &amp;&amp; cached is Stream s) {
    ///       request.CallContext.Remove(VfsPropertyKeys.CachedReadStream);
    ///       return Task.FromResult&lt;Stream?&gt;(s);
    ///   }
    ///   // ... normal open path
    /// </code>
    /// </remarks>
    public const string CachedReadStream = "vfs.node.cachedReadStream";

    // Example node-specific keys for documentation purposes:
    // "ETag"         - HTTP entity tag (S3, REST nodes)
    // "ContentType"  - MIME type (S3, HTTP nodes)
}
