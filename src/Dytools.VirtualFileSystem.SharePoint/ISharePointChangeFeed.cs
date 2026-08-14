using Dytools.VirtualFileSystem;

namespace Dytools.VirtualFileSystem.Nodes.SharePoint;

// SharePoint's delta change feed, exposed as a node capability:
//   vfs.GetCapability<ISharePointChangeFeed>("/team")?.GetChangesAsync(cursor)
//
// Backed by Microsoft Graph's /delta. You own the cursor: pass the one you saved, apply the
// changes, then persist the returned cursor (apply-then-save, idempotent, so a crash re-delivers
// rather than drops). Graph reports upserts as Updated (it can't reliably split create from
// update); deletes arrive as Deleted with a null Info.
public interface ISharePointChangeFeed
{
    Task<SharePointChangeBatch> GetChangesAsync(string? cursor, CancellationToken ct = default);
}

// A page of changes plus the opaque cursor to resume after them.
public sealed record SharePointChangeBatch(IReadOnlyList<SharePointChange> Changes, string Cursor);

// One change. Path is relative to the mount; Info carries current metadata for an upsert and is
// null for a delete.
public sealed record SharePointChange(string Path, SharePointChangeType Type, VfsNodeInfo? Info);

public enum SharePointChangeType { Updated, Deleted }
