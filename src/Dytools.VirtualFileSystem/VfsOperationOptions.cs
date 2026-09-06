namespace Dytools.VirtualFileSystem;

/// <summary>
/// Base for the per-operation options records (<see cref="VfsReadOptions"/>,
/// <see cref="VfsListOptions"/>, <see cref="VfsWriteOptions"/>, <see cref="VfsMetadataOptions"/>).
/// <para>
/// Exists so <see cref="VfsContext"/> can carry whichever options the current call needs in a single
/// reference slot. Only one operation is ever in flight on a context, so a field per operation type
/// would cost eight bytes each to hold nulls for the ones not running - and would grow the context
/// every time an operation gained options. The pipeline stores the concrete instance; readers take it
/// back through the typed accessors on <see cref="VfsContext"/>.
/// </para>
/// </summary>
public abstract record VfsOperationOptions;
