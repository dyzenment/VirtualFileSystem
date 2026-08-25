namespace Dytools.VirtualFileSystem;

/// <summary>
/// Where a deleted entry goes: gone, or into whatever recoverable holding area the backend keeps.
/// </summary>
/// <remarks>
/// The distinction is not a local-disk quirk. A recycle bin, an Azure container with soft delete
/// enabled, and a versioned S3 bucket's delete marker are the same idea wearing three coats: the
/// caller asked for the entry to stop being there, and the backend kept the bytes anyway.
/// </remarks>
public enum VfsDeleteDisposition
{
    /// <summary>
    /// The bytes are gone as far as this VFS is concerned. The default, and what <c>DeleteAsync</c>
    /// has always done.
    /// </summary>
    Permanent,

    /// <summary>
    /// Recoverable, or an error. Throws <see cref="NotSupportedException"/> when the node - or the
    /// particular volume the entry sits on - has no bin to put it in.
    /// <para>
    /// Strict on purpose. Windows silently and permanently destroys anything it cannot recycle (a UNC
    /// path, a volume with the bin turned off, a file larger than the bin's quota), so a request that
    /// quietly degraded into that would be the single worst failure this API could have.
    /// </para>
    /// </summary>
    Recycle,

    /// <summary>
    /// Recoverable where that is possible, permanent everywhere else. Never fails for want of a bin.
    /// Use it when recycling is a courtesy rather than a requirement.
    /// </summary>
    RecycleIfAvailable,
}

/// <summary>
/// Options for a delete. Passed through the pipeline (middleware can read or rewrite it) to the node,
/// mirroring how <see cref="VfsWriteOptions"/> works for writes.
///
/// null options anywhere means <see cref="Default"/>: permanent, recursive.
/// </summary>
public sealed record VfsDeleteOptions : VfsOperationOptions
{
    /// <summary>The defaults: <see cref="VfsDeleteDisposition.Permanent"/>, recursive.</summary>
    public static readonly VfsDeleteOptions Default = new();

    /// <summary>Recycle, or throw when the entry cannot be recycled.</summary>
    public static readonly VfsDeleteOptions Recycle =
        new() { Disposition = VfsDeleteDisposition.Recycle };

    /// <summary>Recycle when possible, permanent delete otherwise.</summary>
    public static readonly VfsDeleteOptions RecycleIfAvailable =
        new() { Disposition = VfsDeleteDisposition.RecycleIfAvailable };

    /// <summary>Where the entry goes. Default <see cref="VfsDeleteDisposition.Permanent"/>.</summary>
    public VfsDeleteDisposition Disposition { get; init; } = VfsDeleteDisposition.Permanent;

    /// <summary>
    /// Whether deleting a directory takes its contents with it. Default <c>true</c>, which is what
    /// <c>DeleteAsync</c> has always done; set <c>false</c> to have a non-empty directory fail instead.
    /// <para>
    /// Separate from <see cref="Disposition"/>: recycling a tree is one operation whatever this says,
    /// because that is how every OS bin models it. Backends with no directories of their own (object
    /// stores, in-memory) ignore it.
    /// </para>
    /// </summary>
    public bool Recursive { get; init; } = true;

    /// <summary>
    /// Lets a bare <see cref="VfsDeleteDisposition"/> stand in wherever options are accepted, so
    /// <c>DeleteAsync(path, VfsDeleteDisposition.Recycle)</c> binds.
    /// </summary>
    public static implicit operator VfsDeleteOptions(VfsDeleteDisposition disposition)
        => disposition switch
        {
            VfsDeleteDisposition.Permanent          => Default,
            VfsDeleteDisposition.Recycle            => Recycle,
            VfsDeleteDisposition.RecycleIfAvailable => RecycleIfAvailable,
            _ => new VfsDeleteOptions { Disposition = disposition },
        };

    /// <summary>
    /// Settles <see cref="Disposition"/> against what the backend can actually do for this entry.
    /// Nodes call it at the top of <c>DeleteAsync</c> and branch on the answer; a node with no bin at
    /// all passes <c>false</c> and gets the correct throw-or-degrade behaviour for free.
    /// </summary>
    /// <param name="available">Whether this entry can in fact be recycled.</param>
    /// <param name="path">The entry, used only to build the exception message.</param>
    /// <returns><c>true</c> to recycle, <c>false</c> to permanently delete.</returns>
    /// <exception cref="NotSupportedException">
    /// <see cref="VfsDeleteDisposition.Recycle"/> was asked for and <paramref name="available"/> is false.
    /// </exception>
    public bool ResolveRecycle(bool available, VfsPath path) => Disposition switch
    {
        VfsDeleteDisposition.Permanent              => false,
        VfsDeleteDisposition.RecycleIfAvailable     => available,
        VfsDeleteDisposition.Recycle when available => true,
        _ => throw new NotSupportedException(
            $"'{path}' cannot be recycled - this backend or volume has no recycle bin. " +
            $"Use {nameof(VfsDeleteDisposition)}.{nameof(VfsDeleteDisposition.RecycleIfAvailable)} " +
            "to fall back to a permanent delete."),
    };
}
