namespace Dytools.VirtualFileSystem;

// -- Symlink capability --------------------------------------------------------
// Marker interface. Nodes implement this to declare that their GetInfoAsync
// may return VfsPropertyKeys.SymlinkTarget in VfsNodeInfo.Properties.
//
// SymlinkMiddleware checks for this capability before calling GetInfoAsync.
// Nodes that do NOT implement it are skipped entirely - zero overhead per read.
//
// If you need to enable symlink checking on a third-party node you cannot modify,
// pass its Type to IVfsBuilder.UseSymlinks(typeof(ThirdPartyNode)).
public interface ISymlinkCapableNode { }

// -- Hard link store -----------------------------------------------------------
// Maps VFS paths to stable content identifiers (e.g. SHA-256 hash) and
// maintains a reference count per content ID. When refcount reaches zero,
// the node may safely delete the backing bytes from storage.
//
// This is a contract only - implementations live in node packages.
// Example: S3HardLinkStore stores path→contentId in DynamoDB or hardlinks.json on S3.
//          InMemoryHardLinkStore stores path→contentId in a ConcurrentDictionary.
//
// Thread-safety: implementations must be thread-safe.
public interface IHardLinkStore
{
    // Returns the content ID for this path, or null if not tracked.
    ValueTask<string?> ResolveContentIdAsync(string relativePath, CancellationToken ct = default);

    // Records that relativePath now points to contentId. Returns new refcount.
    ValueTask<int> AddReferenceAsync(string contentId, string relativePath, CancellationToken ct = default);

    // Removes the path record and decrements refcount. Returns new refcount.
    // If refcount reaches 0, the caller is responsible for deleting the backing bytes.
    ValueTask<int> ReleaseReferenceAsync(string relativePath, CancellationToken ct = default);

    // Returns the current refcount for a content ID (0 if unknown).
    ValueTask<int> GetRefCountAsync(string contentId, CancellationToken ct = default);

    // Enumerates all paths currently pointing to this content ID.
    IAsyncEnumerable<string> GetLinksAsync(string contentId, CancellationToken ct = default);
}

// -- Deduplicating node capability --------------------------------------------
// Implemented by nodes that perform content-addressed storage with reference
// counting. Exposed via VfsNodeBase.GetCapability<IDeduplicatingNode>().
//
// VFS Core never calls this - it is a consumer-facing escape hatch.
// The node handles deduplication transparently inside OpenWriteAsync / DeleteAsync.
//
// Usage:
//   var dedup = vfs.GetCapability<IDeduplicatingNode>("/s3/attachments/logo.png");
//   if (dedup is not null)
//   {
//       var id    = await dedup.HardLinks.ResolveContentIdAsync("attachments/logo.png");
//       var count = await dedup.HardLinks.GetRefCountAsync(id!);
//       // "logo.png is referenced by {count} paths"
//   }
public interface IDeduplicatingNode
{
    IHardLinkStore HardLinks { get; }
}
