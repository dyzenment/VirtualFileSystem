namespace Dytools.VirtualFileSystem;

/// <summary>
/// Whether the entry this capability is bound to would survive a delete in a recoverable form. Reach
/// it with <c>vfs.GetEntryCapability&lt;IRecycling&gt;(path)</c>.
/// <para>
/// A null result means the node does not deal in recycling at all, which is the same answer as a
/// capability whose <see cref="CanRecycleAsync"/> returns false - so a caller that only wants to know
/// whether to bother can treat the two alike.
/// </para>
/// </summary>
/// <remarks>
/// This exists because the answer is per-entry rather than per-node. A single
/// <c>LocalFsNode(@"C:\")</c> mount spans one volume; a mount rooted at a directory that contains
/// mount points spans several, and on Windows a network path under it has no bin while a fixed disk
/// beside it does. Asking the node "do you recycle?" could not have answered that.
/// <para>
/// A true answer is not a guarantee the delete will succeed - it is a cheap pre-check against volume
/// and backend, not an attempt. <see cref="VfsDeleteDisposition.RecycleIfAvailable"/> additionally
/// tolerates a recycle that fails once underway; <see cref="VfsDeleteDisposition.Recycle"/> does not.
/// </para>
/// </remarks>
public interface IRecycling : IEntryCapability
{
    /// <summary>
    /// Whether a delete of this entry with <see cref="VfsDeleteDisposition.Recycle"/> would land it
    /// somewhere recoverable rather than throwing.
    /// </summary>
    ValueTask<bool> CanRecycleAsync(CancellationToken ct = default);
}
