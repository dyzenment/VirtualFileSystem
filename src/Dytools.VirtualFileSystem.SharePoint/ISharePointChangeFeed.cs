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
/// <see cref="SharePointChangeType.Deleted"/> with a null <see cref="SharePointChange.Info"/>.
/// </para>
/// </summary>
public interface ISharePointChangeFeed
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
/// One change. <paramref name="Path"/> is relative to the mount; <paramref name="Info"/> carries
/// current metadata for an upsert and is null for a delete.
/// </summary>
/// <param name="Path">The mount-relative path of the changed item.</param>
/// <param name="Type">Whether the item was upserted or deleted.</param>
/// <param name="Info">Current metadata for an upsert; null for a delete.</param>
public sealed record SharePointChange(string Path, SharePointChangeType Type, VfsNodeInfo? Info);

/// <summary>The kind of change reported by the delta feed.</summary>
public enum SharePointChangeType
{
    /// <summary>The item was created or updated (Graph can't reliably split the two).</summary>
    Updated,

    /// <summary>The item was deleted.</summary>
    Deleted
}
