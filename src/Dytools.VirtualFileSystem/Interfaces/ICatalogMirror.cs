namespace Dytools.VirtualFileSystem;

// Capability exposed by a node that caches its namespace in a catalog: force a refresh of the
// mirror from the backend. Reach it with vfs.GetCapability<ICatalogMirror>(path).
//
// Nodes that keep the mirror fresh incrementally (SharePoint delta) rarely need this; nodes that
// mirror write-through and can't see external changes cheaply (S3, Azure by default) expose it so
// you can re-sync on demand - e.g. from a schedule, or after you know something changed outside
// this VFS.
public interface ICatalogMirror
{
    Task RefreshAsync(CancellationToken ct = default);
}
