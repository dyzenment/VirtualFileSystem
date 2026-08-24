namespace Dytools.VirtualFileSystem;

/// <summary>
/// Reports a content hash for one entry. Reach it with
/// <c>vfs.GetEntryCapability&lt;IContentHashing&gt;(path)</c>; null when the node cannot hash. A
/// decorator may deliberately refuse it - an encryption node should, since a hash of ciphertext
/// answers nothing useful.
/// <para>
/// The two lists are separated because the costs are nothing alike. A native hash is already in
/// metadata the node holds - a SharePoint quickXorHash carried through the delta feed, a dedupe node's
/// stored sha256 - and costs nothing. A computed one reads the entire content.
/// </para>
/// <para>
/// The lists describe the node; whether this particular entry is cheap is a property of the entry. An
/// S3 ETag is an md5 for a single-part upload and meaningless for a multipart one; a dedupe entry has
/// its sha256 only once stored. <see cref="VfsHashBudget"/> settles that per call - so ask for the hash
/// with a ceiling on the cost, rather than asking whether it is available and then asking for it.
/// </para>
/// <para>
/// Every value is lowercase hex, except QuickXor which keeps the base64 Graph reports. Two nodes
/// answering in different encodings would never compare equal, so the encoding is part of the contract
/// rather than each node's choice.
/// </para>
/// </summary>
public interface IContentHashing : IEntryCapability
{
    /// <summary>
    /// Algorithms answerable from metadata alone, cheapest first. Names from
    /// <see cref="VfsHashAlgorithms"/>, though a node may report others.
    /// </summary>
    IReadOnlyList<string> NativeAlgorithms { get; }

    /// <summary>
    /// Algorithms producible by reading the content. Empty when the node cannot compute at all. May
    /// overlap <see cref="NativeAlgorithms"/> - a catalog-backed node usually has its hash stored and
    /// can recompute it when it does not.
    /// </summary>
    IReadOnlyList<string> ComputableAlgorithms { get; }

    /// <summary>
    /// This entry's hash under <paramref name="algorithm"/>, or null when it cannot be had within
    /// <paramref name="budget"/>.
    /// </summary>
    /// <param name="algorithm">A name from <see cref="VfsHashAlgorithms"/>.</param>
    /// <param name="budget">The most this call may cost. See <see cref="VfsHashBudget"/>.</param>
    /// <param name="ct">A token to cancel the work.</param>
    Task<string?> GetHashAsync(
        string algorithm, VfsHashBudget budget = VfsHashBudget.Fetch, CancellationToken ct = default);
}
