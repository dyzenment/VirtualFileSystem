namespace Dytools.VirtualFileSystem;

/// <summary>
/// Marks an interface as a capability addressing <em>one entry</em>. The node hands back an object
/// already bound to that entry, so the interface carries no path parameter at all - a consumer never
/// has to work out where the mount boundary falls.
/// <para>
/// Reach one with <c>vfs.GetEntryCapability&lt;T&gt;(path)</c>.
/// </para>
/// </summary>
/// <remarks>
/// An interface may carry both markers when it can do both - typically per-entry methods for the
/// single case and batch methods for many. Note that acquiring at node level leaves the per-entry
/// methods with no entry to act on; a node exposing both is expected to return an implementation
/// suited to how it was acquired rather than one that fails at runtime.
/// <para>
/// Capabilities are a direct line to the node: they do not run through the middleware pipeline, so
/// nothing a middleware would otherwise enforce applies to them.
/// </para>
/// </remarks>
public interface IEntryCapability;

/// <summary>
/// Marks an interface as a capability belonging to the <em>node</em> rather than to any one entry -
/// refreshing a cache, reading a change feed, or acting on many entries at once. The node hands back
/// an object scoped to the mount, so methods that do address entries take ordinary absolute VFS paths
/// and the node maps them itself.
/// <para>
/// Reach one with <c>vfs.GetNodeCapability&lt;T&gt;(path)</c>, where the path is any path under the
/// mount whose node you want.
/// </para>
/// </summary>
/// <remarks>
/// Capabilities are a direct line to the node: they do not run through the middleware pipeline, so
/// nothing a middleware would otherwise enforce applies to them.
/// </remarks>
public interface INodeCapability;
