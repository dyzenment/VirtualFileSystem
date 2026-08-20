namespace Dytools.VirtualFileSystem;

/// <summary>
/// Options for a directory listing. Passed through the pipeline (middleware can read or
/// rewrite it) to the node. A node honors what it can push down natively; the <c>VfsNodeBase</c>
/// engine supplies the rest (recursion, name matching, kind/hidden filtering) client-side,
/// so every option is always correct - only the cost varies by backend.
///
/// null options anywhere means "the defaults" (<see cref="Default"/>): the listed directory
/// only, files and directories, hidden excluded, standard metadata.
/// </summary>
public sealed record VfsListOptions
{
    /// <summary>The default options: the listed directory only, files and directories, hidden excluded, standard metadata.</summary>
    public static readonly VfsListOptions Default = new();

    // -- Scope -----------------------------------------------------------------

    /// <summary>
    /// false: immediate children of the listed directory only.
    /// true: descend into subdirectories (depth-limited by <see cref="MaxDepth"/>).
    /// </summary>
    public bool Recurse { get; init; }

    /// <summary>
    /// With <see cref="Recurse"/>: null = unlimited; N = at most N levels below the listed directory.
    /// Native on local filesystems; emulated (client-side) on flat stores.
    /// </summary>
    public int? MaxDepth { get; init; }

    // -- Filter ----------------------------------------------------------------

    /// <summary>
    /// Leaf-name glob matched against each entry's name: "*.pdf", "report*", "*".
    /// Supports '*' (any run) and '?' (any single char). null/empty matches everything.
    /// The VFS owns this dialect and validates it once at the boundary; a backend that
    /// cannot push it down has the match applied client-side. Combine with <see cref="Recurse"/> to
    /// match a pattern at every depth (e.g. Recurse + "*.png" = all PNGs in the tree).
    /// </summary>
    public string? SearchPattern { get; init; }

    /// <summary>Which entry kinds to return. Directories are virtual on flat stores.</summary>
    public VfsEntryKind Kind { get; init; } = VfsEntryKind.Both;

    /// <summary>
    /// Include entries flagged hidden. Local-centric; a no-op where the backend has no
    /// hidden concept (nothing is hidden there).
    /// </summary>
    public bool IncludeHidden { get; init; }

    // -- Projection ------------------------------------------------------------

    /// <summary>
    /// How much per-entry metadata to hydrate. <see cref="VfsListDetail.NamesOnly"/> is cheapest;
    /// <see cref="VfsListDetail.WithMetadata"/> may cost extra round-trips on some backends (e.g. S3 needs a HEAD per object).
    /// </summary>
    public VfsListDetail Detail { get; init; } = VfsListDetail.Standard;

    // -- Execution -------------------------------------------------------------

    /// <summary>
    /// Strict guardrail: when a <see cref="SearchPattern"/> would force a full backend scan (a node that
    /// cannot push it down - e.g. a suffix pattern on S3/Azure), throw <see cref="NotSupportedException"/>
    /// before enumerating instead of silently scanning everything. Default off = fall back
    /// to a client-side scan.
    /// </summary>
    public bool ThrowIfPatternNotSupported { get; init; }
}

/// <summary>Which entry kinds a listing returns.</summary>
[System.Flags]
public enum VfsEntryKind
{
    /// <summary>File entries only.</summary>
    Files       = 1,
    /// <summary>Directory entries only. Directories are virtual on flat stores.</summary>
    Directories = 2,
    /// <summary>Both files and directories.</summary>
    Both        = Files | Directories,
}

/// <summary>How much metadata a listing hydrates per entry.</summary>
public enum VfsListDetail
{
    /// <summary>Path and kind only.</summary>
    NamesOnly,
    /// <summary>Path and kind, plus size and timestamps (cheap list fields).</summary>
    Standard,
    /// <summary>Path, kind, size, timestamps, plus node-specific Properties (may cost extra round-trips).</summary>
    WithMetadata,
}
