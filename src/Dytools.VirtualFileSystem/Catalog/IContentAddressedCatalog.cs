using System.Runtime.CompilerServices;

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

    /// <summary>
    /// Every entry currently referencing <paramref name="contentId"/> - the reverse of
    /// <see cref="CatalogEntry.ContentId"/>, and what "which paths share these bytes" resolves to.
    /// <para>
    /// Non-unique by nature where a node dedupes: one copy of the bytes behind many paths is the
    /// point, so this yields zero, one, or many. A node that keeps the key unique within its
    /// partition - a mirror storing a backend item id - can treat a second match as corruption to
    /// repair rather than a normal result.
    /// </para>
    /// <para>
    /// <see cref="ReferenceCountAsync"/> counts the same set and stays separate because a store can
    /// answer a count without materializing the rows; implementations overriding one should keep the
    /// other in step. The default walks the whole namespace: correct anywhere, but O(entries).
    /// Implementations that index <see cref="CatalogEntry.ContentId"/>, or that already hold the
    /// namespace in memory, override it.
    /// </para>
    /// </summary>
    /// <param name="contentId">The storage key to find referencing entries for.</param>
    /// <param name="ct">A token to cancel the enumeration.</param>
    async IAsyncEnumerable<CatalogEntry> ListByContentIdAsync(
        string contentId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pending = new Stack<VfsPath>();
        pending.Push(VfsPath.From(""));
        while (pending.Count > 0)
        {
            await foreach (var e in ListChildrenAsync(pending.Pop(), ct))
            {
                if (e.IsDirectory) pending.Push(e.Path);
                else if (e.ContentId == contentId) yield return e;
            }
        }
    }
}
