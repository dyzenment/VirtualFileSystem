namespace Dytools.VirtualFileSystem;

/// <summary>
/// Base for backend-specific write options a node package defines - an S3 checksum algorithm, a
/// storage tier, a content-disposition. Attach one to <see cref="VfsWriteOptions.NodeOptions"/>; the
/// node that ends up writing the bytes casts to its own type and ignores anything else.
/// <para>
/// One slot rather than a field per backend feature: core stays ignorant of what any given node can
/// do, and <see cref="VfsWriteOptions"/> does not grow every time a node gains a knob. Decorators pass
/// it through untouched, since only the node actually storing the bytes can act on it.
/// </para>
/// </summary>
public abstract record VfsNodeWriteOptions;
