using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Dytools.VirtualFileSystem;

namespace Dytools.VirtualFileSystem.Nodes.InMemory;

// Thread-safe in-memory key-value node.
// Keys are normalised relative paths. Values are raw byte arrays.
// ADS (StreamName) is supported - stored as a distinct key: "path:streamName".
// QueryString is ignored - in-memory storage has no concept of query parameters.
//
// Read path (OpenReadAsync, ExistsAsync, GetInfoAsync) is zero-alloc on .NET 9+:
//   the key is built into a stackalloc buffer and looked up via
//   Dictionary.GetAlternateLookup<ReadOnlySpan<char>>().
// Write path allocates one string for the dictionary key.
public sealed class InMemoryKvNode : VfsNodeBase
{
    // Dictionary rather than ConcurrentDictionary - GetAlternateLookup requires it.
    // Access is guarded by a ReaderWriterLockSlim.
    private readonly Dictionary<string, byte[]>        _store = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReaderWriterLockSlim              _lock  = new(LockRecursionPolicy.NoRecursion);
    // Cached AlternateLookup - valid for the lifetime of _store (same dictionary instance).
    private readonly Dictionary<string, byte[]>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public InMemoryKvNode() => _lookup = _store.GetAlternateLookup<ReadOnlySpan<char>>();

    public override Task<Stream?> OpenReadAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        Span<char> buf = stackalloc char[VfsPath.MaxLength];
        int keyLen     = FillKey(request.Path.PathSpan, request.Path.StreamSpan, buf);
        var keySpan    = buf[..keyLen];

        _lock.EnterReadLock();
        try
        {
            if (!_lookup.TryGetValue(keySpan, out var bytes))
                return Task.FromResult<Stream?>(null);
            return Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false));
        }
        finally { _lock.ExitReadLock(); }
    }

    public override Task<Stream> OpenWriteAsync(VfsNodeRequest request, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
    {
        var key = BuildKey(request.Path.PathSpan, request.Path.StreamSpan); // one string alloc for writes

        _lock.EnterWriteLock();
        try
        {
            if (mode == VfsWriteMode.CreateNew && _store.ContainsKey(key))
                throw new IOException($"Key already exists: {key}");

            byte[]? existing = null;
            if (mode == VfsWriteMode.Append)
                _store.TryGetValue(key, out existing);

            return Task.FromResult<Stream>(
                new CaptureStream(existing ?? Array.Empty<byte>(), data =>
                {
                    _lock.EnterWriteLock();
                    try   { _store[key] = data; }
                    finally { _lock.ExitWriteLock(); }
                }));
        }
        finally { _lock.ExitWriteLock(); }
    }

    public override Task DeleteAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        var key = BuildKey(request.Path.PathSpan, request.Path.StreamSpan);
        _lock.EnterWriteLock();
        try   { _store.Remove(key); }
        finally { _lock.ExitWriteLock(); }
        return Task.CompletedTask;
    }

    public override Task CopyAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        Span<char> buf = stackalloc char[VfsPath.MaxLength];
        int srcLen = FillKey(src.Path.PathSpan, src.Path.StreamSpan, buf);

        _lock.EnterReadLock();
        byte[]? bytes;
        try   { _lookup.TryGetValue(buf[..srcLen], out bytes); }
        finally { _lock.ExitReadLock(); }

        if (bytes is null) return Task.CompletedTask;

        var dstKey = BuildKey(dst.Path.PathSpan, dst.Path.StreamSpan);
        _lock.EnterWriteLock();
        try   { _store[dstKey] = (byte[])bytes.Clone(); }
        finally { _lock.ExitWriteLock(); }
        return Task.CompletedTask;
    }

    public override Task MoveAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
    {
        var srcKey = BuildKey(src.Path.PathSpan, src.Path.StreamSpan);
        var dstKey = BuildKey(dst.Path.PathSpan, dst.Path.StreamSpan);
        _lock.EnterWriteLock();
        try
        {
            if (_store.Remove(srcKey, out var bytes))
                _store[dstKey] = bytes;
        }
        finally { _lock.ExitWriteLock(); }
        return Task.CompletedTask;
    }

    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var relBase = new string(request.Path.PathSpan).TrimEnd('/'); // "" when listing mount root
        var prefix  = relBase.Length > 0 ? relBase + "/" : "";

        List<(string Key, int Size)> snapshot;
        _lock.EnterReadLock();
        try
        {
            snapshot = new List<(string, int)>(_store.Count);
            foreach (var (k, v) in _store)
                if (prefix.Length == 0 || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    snapshot.Add((k, v.Length));
        }
        finally { _lock.ExitReadLock(); }

        foreach (var (k, size) in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            var remainder = prefix.Length > 0 ? k[prefix.Length..] : k;
            if (remainder.Contains('/')) continue; // only direct children

            yield return new VfsNodeInfo
            {
                RelativePath = relBase.Length > 0 ? VfsPath.From($"{relBase}/{remainder}") : VfsPath.From(remainder),
                IsFile       = true,
                IsDirectory  = false,
                SizeBytes    = size,
                ModifiedAt   = DateTimeOffset.UtcNow,
            };
        }
    }

    public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest request, CancellationToken ct = default)
    {
        Span<char> buf = stackalloc char[VfsPath.MaxLength];
        int keyLen     = FillKey(request.Path.PathSpan, request.Path.StreamSpan, buf);

        _lock.EnterReadLock();
        int? size;
        try
        {
            size = _lookup.TryGetValue(buf[..keyLen], out var bytes) ? bytes.Length : null;
        }
        finally { _lock.ExitReadLock(); }

        if (size is null) return Task.FromResult<VfsNodeInfo?>(null);

        return Task.FromResult<VfsNodeInfo?>(new VfsNodeInfo
        {
            RelativePath = request.Path,
            IsFile       = true,
            IsDirectory  = false,
            SizeBytes    = size.Value,
            ModifiedAt   = DateTimeOffset.UtcNow,
        });
    }

    // -- Key helpers -----------------------------------------------------------

    // Fills a caller-supplied stackalloc buffer with "relative[:stream]".
    // Returns the number of chars written. Case preserved - dict is OrdinalIgnoreCase.
    private static int FillKey(ReadOnlySpan<char> relSpan, ReadOnlySpan<char> streamSpan, Span<char> buf)
    {
        relSpan.CopyTo(buf);
        int len = relSpan.Length;

        if (!streamSpan.IsEmpty)
        {
            buf[len++] = ':';
            streamSpan.CopyTo(buf[len..]);
            len += streamSpan.Length;
        }

        return len;
    }

    // Allocates a key string - used only on write paths.
    private static string BuildKey(ReadOnlySpan<char> relSpan, ReadOnlySpan<char> streamSpan)
    {
        Span<char> buf = stackalloc char[VfsPath.MaxLength];
        int len = FillKey(relSpan, streamSpan, buf);
        return new string(buf[..len]);
    }
}

// Buffers writes; on Dispose/DisposeAsync commits to the store atomically.
internal sealed class CaptureStream(byte[] seed, Action<byte[]> onComplete) : Stream
{
    // new MemoryStream(byte[]) is non-expandable. For append we need a resizable buffer
    // pre-seeded with the existing content.
    private readonly MemoryStream _buf = seed.Length > 0 ? Seed(seed) : new MemoryStream();

    private static MemoryStream Seed(byte[] data)
    {
        var ms = new MemoryStream(data.Length + 64);
        ms.Write(data, 0, data.Length);
        return ms;
    }
    private bool _completed;

    public override bool CanRead  => false;
    public override bool CanSeek  => false;
    public override bool CanWrite => true;
    public override long Length   => _buf.Length;
    public override long Position { get => _buf.Position; set => throw new NotSupportedException(); }

    public override void  Write(byte[] buffer, int offset, int count)            => _buf.Write(buffer, offset, count);
    public override Task  WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _buf.WriteAsync(buffer, offset, count, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _buf.WriteAsync(buffer, ct);
    public override void  Flush() { }
    public override int   Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long  Seek(long offset, SeekOrigin origin)       => throw new NotSupportedException();
    public override void  SetLength(long value)                      => throw new NotSupportedException();

    protected override void Dispose(bool disposing) { if (disposing) Complete(); base.Dispose(disposing); }
    public override ValueTask DisposeAsync()        { Complete(); return base.DisposeAsync(); }

    private void Complete()
    {
        if (_completed) return;
        _completed = true;
        onComplete(_buf.ToArray());
    }
}
