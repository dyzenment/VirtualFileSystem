namespace Dytools.VirtualFileSystem;

// Per-call operation options - 8 bytes, stored directly on VfsContext (no heap alloc).
// All options default to zero, which maps to the sensible default for each field.
//
// Bit layout (long, 64 bits):
//   bits 0-1   WriteMode    (0=Create, 1=Append, 2=CreateNew)
//   bits 2-63  reserved for future options (CopyOverwrite, MoveOverwrite, ListHashDepth, etc.)
//
// Middleware reads options via ctx.Options; sets options via ctx.Options = ctx.Options.With*(...).
// New per-operation parameters never require changing IVfsMiddleware or VfsPipeline signatures.
public readonly struct VfsCallOptions
{
    private readonly long _flags;

    private VfsCallOptions(long flags) => _flags = flags;

    // -- WriteMode - bits 0-1 --------------------------------------------------

    private const int  WriteModeShift = 0;
    private const long WriteModeMask  = 0b11L;

    /// <summary>Open mode for write operations. Defaults to <see cref="VfsWriteMode.Create"/>.</summary>
    public VfsWriteMode WriteMode => (VfsWriteMode)((_flags >> WriteModeShift) & WriteModeMask);

    /// <summary>Returns a copy of these options with <see cref="WriteMode"/> changed.</summary>
    public VfsCallOptions WithWriteMode(VfsWriteMode mode)
        => new((_flags & ~(WriteModeMask << WriteModeShift)) | ((long)mode << WriteModeShift));

    // -- Reserved bits for future options -------------------------------------
    // bit  2  - CopyOverwrite (future)
    // bit  3  - MoveOverwrite (future)
    // bits 4-5 - ListHashDepth (future)
    // bits 6-63 - free
}
