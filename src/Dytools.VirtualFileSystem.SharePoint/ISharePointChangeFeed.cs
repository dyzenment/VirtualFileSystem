using Dytools.VirtualFileSystem;

namespace Dytools.VirtualFileSystem.Nodes.SharePoint;

/// <summary>
/// SharePoint's delta change feed, exposed as a node capability:
/// <c>vfs.GetCapability&lt;ISharePointChangeFeed&gt;("/team")?.GetChangesAsync(cursor)</c>
/// <para>
/// Backed by Microsoft Graph's /delta. You own the cursor: pass the one you saved, apply the
/// changes, then persist the returned cursor (apply-then-save, idempotent, so a crash re-delivers
/// rather than drops). Graph reports upserts as <see cref="SharePointChangeType.Updated"/> (it
/// can't reliably split create from update); deletes arrive as
/// <see cref="SharePointChangeType.Deleted"/> with a null <see cref="SharePointChange.Info"/> and,
/// usually, a null <see cref="SharePointChange.Path"/> - match those on
/// <see cref="SharePointChange.Id"/>.
/// </para>
/// </summary>
public interface ISharePointChangeFeed : INodeCapability
{
    /// <summary>
    /// Fetches the changes since <paramref name="cursor"/> (null for a fresh full delta).
    /// </summary>
    /// <param name="cursor">The opaque cursor returned by a previous batch, or null to start fresh.</param>
    /// <param name="ct">A token to cancel the request.</param>
    /// <returns>A batch of changes plus the cursor to resume from next time.</returns>
    Task<SharePointChangeBatch> GetChangesAsync(string? cursor, CancellationToken ct = default);
}

/// <summary>A page of changes plus the opaque cursor to resume after them.</summary>
/// <param name="Changes">The changes in this batch.</param>
/// <param name="Cursor">The opaque cursor to resume after these changes.</param>
public sealed record SharePointChangeBatch(IReadOnlyList<SharePointChange> Changes, string Cursor);

/// <summary>
/// One change, identified by <paramref name="Id"/> - the driveItem id, which is stable across renames
/// and moves and is the only field a deletion is guaranteed to report.
/// <para>
/// <paramref name="Path"/> is best-effort and NULL for a deletion whose tombstone carried no name or
/// parent path, which is the normal shape. Match a delete on <paramref name="Id"/>; treat the path as
/// context only.
/// </para>
/// </summary>
/// <param name="Path">The mount-relative path, or null when the change did not report one.</param>
/// <param name="Type">Whether the item was upserted or deleted.</param>
/// <param name="Info">Current metadata for an upsert; null for a delete.</param>
/// <param name="Id">The driveItem id. Always present on a deletion; null only for a malformed item.</param>
public sealed record SharePointChange(string? Path, SharePointChangeType Type, VfsNodeInfo? Info, string? Id);

/// <summary>The kind of change reported by the delta feed.</summary>
public enum SharePointChangeType
{
    /// <summary>The item was created or updated (Graph can't reliably split the two).</summary>
    Updated,

    /// <summary>The item was deleted.</summary>
    Deleted
}
