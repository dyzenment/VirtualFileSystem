namespace Dytools.VirtualFileSystem;

// Constants for VfsNodeInfo.Properties and VfsEntryInfo.Properties keys.
// Nodes use these when surfacing optional metadata that has no place in the
// cross-platform core fields. Consumers use them for safe key access.
public static class VfsPropertyKeys
{
    // Set by LocalFsNode when the OS reports a reparse point or Unix symlink.
    // Value: bool (true)
    public const string PhysicalSymlink = "PhysicalSymlink";

    // Set by nodes that support node-level symlinks.
    // Value: string - the absolute VFS path this entry points to.
    // SymlinkMiddleware reads this key to follow the pointer.
    public const string SymlinkTarget = "SymlinkTarget";

    // Set by deduplicating nodes (IDeduplicatingNode) on GetInfo operations.
    // Value: string - stable content identifier (e.g. lowercase hex SHA-256).
    public const string ContentId = "ContentId";

    // Parking slot for a stream opened by a node's GetInfoAsync during symlink detection.
    // Value: Stream - seekable, seeked back to position 0.
    //
    // Lifecycle:
    //   GetInfoAsync  - node opens stream to check magic header, stores it here if NOT a symlink.
    //                   Node must NOT dispose the stream after storing it.
    //   FollowAsync   - if a symlink IS found and the context is rerouted, SymlinkMiddleware
    //                   disposes the cached stream (it belongs to the pointer file, not the target).
    //   OpenReadAsync - node checks CallContext for its own cached stream and returns it directly,
    //                   skipping a second open. Node removes the entry and owns disposal from here.
    //
    // Usage in GetInfoAsync (node that implements ISymlinkCapableNode):
    //   if (!isSymlink && stream.CanSeek) {
    //       stream.Seek(0, SeekOrigin.Begin);
    //       request.CallContext?[VfsPropertyKeys.CachedReadStream] = stream;
    //       // do NOT dispose - node will reclaim it in OpenReadAsync
    //   }
    //
    // Usage in OpenReadAsync (same node):
    //   if (request.CallContext?.TryGetValue(VfsPropertyKeys.CachedReadStream, out var cached) == true
    //       && cached is Stream s) {
    //       request.CallContext.Remove(VfsPropertyKeys.CachedReadStream);
    //       return Task.FromResult<Stream?>(s);
    //   }
    //   // ... normal open path
    public const string CachedReadStream = "vfs.node.cachedReadStream";

    // Example node-specific keys for documentation purposes:
    // "ETag"         - HTTP entity tag (S3, REST nodes)
    // "ContentType"  - MIME type (S3, HTTP nodes)
}
