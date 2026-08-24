namespace Dytools.VirtualFileSystem.Catalog;

/// <summary>
/// A catalog that additionally indexes entries by content, for nodes that store bytes once and point
/// many paths at them.
/// <para>
/// Split out from <see cref="IVfsCatalog"/> because the two roles have different audiences. A node
/// that mirrors a remote namespace - SharePoint, Azure, S3 - needs the path operations and nothing
/// else; obliging it to answer "how many paths reference this content" is asking for an index it will
/// never read. Only a content-addressed node needs both.
/// </para>
/// </summary>
public interface IContentAddressedCatalog : IVfsCatalog
{
    /// <summary>
    /// How many entries currently reference <paramref name="contentId"/>. Zero means the stored bytes
    /// are unreferenced and can be collected.
    /// </summary>
    ValueTask<int> ReferenceCountAsync(string contentId, CancellationToken ct = default);

    /// <summary>
    /// The storage key already holding content with this hash, or null when it is not stored yet -
    /// what makes a second copy of the same bytes cost nothing.
    /// </summary>
    ValueTask<string?> FindContentIdByHashAsync(string hash, CancellationToken ct = default);
}
