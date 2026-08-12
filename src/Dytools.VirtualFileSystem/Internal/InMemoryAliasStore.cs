using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Dytools.VirtualFileSystem.Internal;

internal sealed class InMemoryAliasStore : IVfsAliasStore
{
    private readonly ConcurrentDictionary<string, string> _aliases =
        new(StringComparer.OrdinalIgnoreCase);

    public Task SaveAsync(string alias, string target, CancellationToken ct = default)
    {
        _aliases[alias] = target;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string alias, CancellationToken ct = default)
    {
        _aliases.TryRemove(alias, out _);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<(string Alias, string Target)> LoadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (alias, target) in _aliases)
        {
            ct.ThrowIfCancellationRequested();
            yield return (alias, target);
        }
    }
}
