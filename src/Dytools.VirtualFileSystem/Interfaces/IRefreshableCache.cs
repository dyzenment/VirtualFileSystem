namespace Dytools.VirtualFileSystem;

/// <summary>
/// A node that caches its namespace and can be told to re-read it from the backend. Reach it with
/// <c>vfs.GetNodeCapability&lt;IRefreshableCache&gt;(path)</c>.
/// <para>
/// Named for what it offers rather than how it is built: a consumer asking to refresh should not have
/// to know a catalog is involved, and a node that later caches some other way still refreshes.
/// </para>
/// <para>
/// Nodes that keep the cache current incrementally (SharePoint's delta feed) rarely need this. Nodes
/// that write through and cannot see outside changes cheaply (S3, and Azure by default) expose it so
/// you can re-sync deliberately - on a schedule, or once you know something changed outside this VFS.
/// </para>
/// </summary>
public interface IRefreshableCache : INodeCapability
{
    /// <summary>Re-reads the cached namespace from the backend.</summary>
    Task RefreshAsync(CancellationToken ct = default);
}
