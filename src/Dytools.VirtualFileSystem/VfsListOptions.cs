namespace Dytools.VirtualFileSystem;

// Options for a directory listing. Passed through the pipeline (middleware can read or
// rewrite it) to the node. A node honors what it can push down natively; the VfsNodeBase
// engine supplies the rest (recursion, name matching, kind/hidden filtering) client-side,
// so every option is always correct - only the cost varies by backend.
//
// null options anywhere means "the defaults" (VfsListOptions.Default): the listed directory
// only, files and directories, hidden excluded, standard metadata.
public sealed record VfsListOptions
{
    public static readonly VfsListOptions Default = new();

    // -- Scope -----------------------------------------------------------------

    // false: immediate children of the listed directory only.
    // true: descend into subdirectories (depth-limited by MaxDepth).
    public bool Recurse { get; init; }

    // With Recurse: null = unlimited; N = at most N levels below the listed directory.
    // Native on local filesystems; emulated (client-side) on flat stores.
    public int? MaxDepth { get; init; }

    // -- Filter ----------------------------------------------------------------

    // Leaf-name glob matched against each entry's name: "*.pdf", "report*", "*".
    // Supports '*' (any run) and '?' (any single char). null/empty matches everything.
    // The VFS owns this dialect and validates it once at the boundary; a backend that
    // cannot push it down has the match applied client-side. Combine with Recurse to
    // match a pattern at every depth (e.g. Recurse + "*.png" = all PNGs in the tree).
    public string? SearchPattern { get; init; }

    // Which entry kinds to return. Directories are virtual on flat stores.
    public VfsEntryKind Kind { get; init; } = VfsEntryKind.Both;

    // Include entries flagged hidden. Local-centric; a no-op where the backend has no
    // hidden concept (nothing is hidden there).
    public bool IncludeHidden { get; init; }

    // -- Projection ------------------------------------------------------------

    // How much per-entry metadata to hydrate. NamesOnly is cheapest; WithMetadata may
    // cost extra round-trips on some backends (e.g. S3 needs a HEAD per object).
    public VfsListDetail Detail { get; init; } = VfsListDetail.Standard;

    // -- Execution -------------------------------------------------------------

    // Strict guardrail: when a SearchPattern would force a full backend scan (a node that
    // cannot push it down - e.g. a suffix pattern on S3/Azure), throw NotSupportedException
    // before enumerating instead of silently scanning everything. Default off = fall back
    // to a client-side scan.
    public bool ThrowIfPatternNotSupported { get; init; }
}

// Which entry kinds a listing returns.
[System.Flags]
public enum VfsEntryKind
{
    Files       = 1,
    Directories = 2,
    Both        = Files | Directories,
}

// How much metadata a listing hydrates per entry.
public enum VfsListDetail
{
    NamesOnly,     // path/kind only
    Standard,      // + size, timestamps (cheap list fields)
    WithMetadata,  // + node-specific Properties (may cost extra round-trips)
}
