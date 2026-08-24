namespace Dytools.VirtualFileSystem;

/// <summary>
/// How much work a hash request may cost. Each level authorises everything below it, so the value is
/// a ceiling rather than a strategy - a node answers as cheaply as it can and only reaches for the
/// dearer options when allowed.
/// <para>
/// The default is <see cref="Fetch"/> deliberately. Reading content is unbounded: a single innocent
/// call against a large remote item would download all of it, and a loop over a listing would download
/// the library. Anything that expensive should be asked for explicitly.
/// </para>
/// </summary>
public enum VfsHashBudget
{
    /// <summary>
    /// Only what the node already holds - a cached catalog row, nothing else. No request of any kind.
    /// Returns null when it does not have one, which is the answer that lets a caller fall back to
    /// something cheap like comparing sizes.
    /// </summary>
    Cached = 0,

    /// <summary>
    /// As <see cref="Cached"/>, plus one metadata request for a hash the backend already computed -
    /// a SharePoint quickXorHash, an Azure Content-MD5, an S3 checksum. Bounded and small, but it is
    /// a round-trip per entry, so it is the wrong level for a loop over thousands of them.
    /// </summary>
    Fetch = 1,

    /// <summary>
    /// As <see cref="Fetch"/>, plus reading the entry's content to compute the hash. Cheap on local
    /// disk; on a remote store it means downloading the whole thing.
    /// <para>
    /// A node with a catalog also parks the result on the entry's row - that is its own cache, costs a
    /// row write, and is discarded when the entry changes, so there is nothing to opt into.
    /// </para>
    /// </summary>
    Compute = 2,

    /// <summary>
    /// As <see cref="Compute"/>, plus writing the hash back to the backing service, so anything else
    /// looking at that object sees it too.
    /// <para>
    /// This is the level that mutates someone else's data - Azure blob metadata, an S3 object tag -
    /// which is why it is separate from the local caching that <see cref="Compute"/> already does.
    /// A node with nowhere remote to put it behaves exactly as <see cref="Compute"/> would; that is
    /// every node without a service to write to, which today means local disk, in-memory and dedupe.
    /// </para>
    /// </summary>
    ComputeAndStore = 3,
}
