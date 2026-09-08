namespace Dytools.VirtualFileSystem;

/// <summary>
/// One VFS path that reaches a given host path, from <c>GetVfsPathCandidates</c>.
/// <para>
/// A host path can be reachable more than once - two mounts covering overlapping roots, or an alias
/// offering a second route to the same mount - so the answer is labelled rather than left to be
/// inferred from its position in the list.
/// </para>
/// </summary>
public sealed record VfsPathCandidate
{
    /// <summary>The full VFS path.</summary>
    public required string Path { get; init; }

    /// <summary>The mount this path routes through.</summary>
    public required string MountPoint { get; init; }

    /// <summary>
    /// The alias key this path goes through, or null when it addresses the mount directly.
    /// For an internal mount this is never null: an alias is the only way to reach one.
    /// </summary>
    public string? ViaAlias { get; init; }

    /// <summary>
    /// Whether the mount is internal. A direct path to one throws when used, so these are excluded
    /// unless <see cref="VfsPathLookupOptions.IncludeInternal"/> asked for them.
    /// </summary>
    public bool IsInternal { get; init; }
}

/// <summary>Options for looking up the VFS paths that reach a host path.</summary>
public sealed record VfsPathLookupOptions
{
    /// <summary>The defaults: usable paths only - internal mounts excluded, aliases included.</summary>
    public static readonly VfsPathLookupOptions Default = new();

    /// <summary>
    /// Include the direct path to an internal mount. Off by default: the registry refuses a direct
    /// resolve of an internal mount, so such a path would throw the moment anything used it. Turn it
    /// on to inspect the routing rather than to act on it.
    /// </summary>
    public bool IncludeInternal { get; init; }

    /// <summary>
    /// Offer alias routes alongside the direct mount path. On by default, and load-bearing rather
    /// than cosmetic: where the covering mount is internal, a public alias is the only usable answer.
    /// </summary>
    public bool IncludeAliases { get; init; } = true;
}
