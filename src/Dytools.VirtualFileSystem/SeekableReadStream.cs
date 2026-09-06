using System.Buffers;

namespace Dytools.VirtualFileSystem;

/// <summary>
/// Gives a forward-only stream full random access, buffering what has been read so that a consumer
/// can seek back into it. Small streams stay in memory; once the retained bytes cross
/// <see cref="DefaultMemoryThreshold"/> the buffer spills to a temp file that deletes itself on close.
/// <para>
/// The case this exists for is the utility that will not touch a stream unless
/// <see cref="Stream.CanSeek"/> is true - an image or document reader that wants to parse a header,
/// rewind, and then read forward. Several backends here cannot offer that: <c>S3Node</c>,
/// <c>AzureBlobNode</c> and <c>SharePointNode</c> hand back a live HTTP response body, where
/// <see cref="Stream.CanSeek"/> is false and <see cref="Stream.Length"/> throws.
/// </para>
/// <para>
/// Buffering is lazy. Crossing the threshold moves what is already held to disk; it does not drain
/// the source. A consumer that reads 2 MB of a 2 GB object pulls 2 MB, whatever the threshold is.
/// Two things do force the whole source to be read: <see cref="Length"/>, and a seek relative to
/// <see cref="SeekOrigin.End"/> - neither can be answered without finding the end. Avoid both on a
/// large remote entry, or take the size from <c>GetInfoAsync</c> instead, which costs one metadata
/// call rather than a download.
/// </para>
/// <para>
/// A source that is already seekable is passed straight through and nothing is buffered or copied, so
/// wrapping a <c>LocalFsNode</c> stream costs only the wrapper object.
/// </para>
/// </summary>
/// <remarks>
/// Not thread-safe, in line with <see cref="Stream"/> itself. Reads are served through the async path
/// where the caller uses one; the synchronous overrides do not block on async work.
/// </remarks>
public sealed class SeekableReadStream : Stream
{
    /// <summary>Retained bytes held in memory before the buffer spills to a temp file - 1 MiB.</summary>
    public const long DefaultMemoryThreshold = 1024 * 1024;

    // Chunk used when filling ahead for a seek or a length probe, where the bytes are not going to a
    // caller's buffer and so need somewhere of their own to land.
    private const int FillChunk = 81920;

    private readonly Stream _inner;
    private readonly bool   _ownsInner;
    private readonly long   _memoryThreshold;
    private readonly bool   _passThrough;

    // The random-access store for everything pulled from _inner: a MemoryStream, then a temp file
    // after a spill. Null in pass-through mode, where _inner is the store.
    private Stream? _store;
    private string? _spillPath;

    // _position is the only authority for where this stream is. _store's own position is set
    // immediately before each read and never trusted between calls.
    private long _position;
    private long _buffered;          // bytes of _inner pulled into _store
    private bool _innerExhausted;
    private bool _disposed;

    /// <summary>
    /// Wraps <paramref name="inner"/>. When it is already seekable the wrapper delegates to it and
    /// buffers nothing.
    /// </summary>
    /// <param name="inner">The stream to make seekable.</param>
    /// <param name="ownsInner">Whether disposing this stream also disposes <paramref name="inner"/>.</param>
    /// <param name="memoryThreshold">
    /// Retained bytes held in memory before spilling to a temp file. Zero spills on the first read.
    /// </param>
    public SeekableReadStream(
        Stream inner, bool ownsInner = false, long memoryThreshold = DefaultMemoryThreshold)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(memoryThreshold);
        if (!inner.CanRead)
            throw new ArgumentException("The stream to wrap must be readable.", nameof(inner));

        _inner           = inner;
        _ownsInner       = ownsInner;
        _memoryThreshold = memoryThreshold;
        _passThrough     = inner.CanSeek;

        if (!_passThrough) _store = new MemoryStream();
    }

    /// <summary>
    /// Raised once, with the temp file's path, when the retained bytes outgrow the memory threshold.
    /// The file is deleted when this stream is disposed.
    /// </summary>
    public event EventHandler<string>? SpilledToDisk;

    /// <summary>Whether the retained bytes have outgrown the memory threshold and moved to a temp file.</summary>
    public bool HasSpilledToDisk => _spillPath is not null;

    /// <summary>Whether the source was already seekable, in which case this stream is a pass-through.</summary>
    public bool IsPassThrough => _passThrough;

    // -- Stream shape ----------------------------------------------------------

    /// <inheritdoc/>
    public override bool CanRead  => !_disposed;
    /// <inheritdoc/>
    public override bool CanSeek  => !_disposed;
    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <summary>
    /// The length of the entry. On a non-seekable source this reads the source to its end - see the
    /// note on the class about avoiding it for large remote entries.
    /// </summary>
    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_passThrough) return _inner.Length;
            Fill(long.MaxValue);
            return _buffered;
        }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _passThrough ? _inner.Position : _position;
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc/>
    public override void Flush() { /* read-only */ }

    /// <summary>Not supported - this stream is read-only.</summary>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Not supported - this stream is read-only.</summary>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // -- Seeking ---------------------------------------------------------------

    /// <summary>
    /// Moves to <paramref name="offset"/>, filling the buffer from the source first when the target
    /// lies beyond what has been read. <see cref="SeekOrigin.End"/> reads the source to its end.
    /// </summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_passThrough) return _inner.Seek(offset, origin);

        // Length (the End case) can itself fill the buffer, so target is computed before anything
        // else is touched and _position is assigned exactly once, at the end.
        var target = origin switch
        {
            SeekOrigin.Begin   => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End     => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown seek origin."),
        };

        if (target < 0) throw new IOException("Cannot seek to a negative position.");

        if (target > _buffered) Fill(target);
        _position = target;
        return _position;
    }

    // -- Reading ---------------------------------------------------------------

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_passThrough) return _inner.Read(buffer);
        if (buffer.IsEmpty) return 0;

        // Inside what is already retained: serve from the store.
        if (_position < _buffered)
        {
            _store!.Position = _position;
            var served = _store.Read(buffer[..(int)Math.Min(_buffered - _position, buffer.Length)]);
            _position += served;
            return served;
        }

        if (_innerExhausted) return 0;

        // At the frontier: pull straight into the caller's buffer, then retain what was pulled.
        var pulled = _inner.Read(buffer);
        if (pulled == 0) { _innerExhausted = true; return 0; }
        Append(buffer[..pulled]);
        _position += pulled;
        return pulled;
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_passThrough) return await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (buffer.IsEmpty) return 0;

        if (_position < _buffered)
        {
            _store!.Position = _position;
            var window = buffer[..(int)Math.Min(_buffered - _position, buffer.Length)];
            var served = await _store.ReadAsync(window, ct).ConfigureAwait(false);
            _position += served;
            return served;
        }

        if (_innerExhausted) return 0;

        var pulled = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (pulled == 0) { _innerExhausted = true; return 0; }
        await AppendAsync(buffer[..pulled], ct).ConfigureAwait(false);
        _position += pulled;
        return pulled;
    }

    // -- Buffer management -----------------------------------------------------

    // Pulls from the source until _buffered reaches upTo, or the source ends. long.MaxValue drains it.
    private void Fill(long upTo)
    {
        if (_innerExhausted) return;

        var scratch = ArrayPool<byte>.Shared.Rent(FillChunk);
        try
        {
            while (_buffered < upTo)
            {
                var want = (int)Math.Min(scratch.Length, upTo - _buffered);
                var read = _inner.Read(scratch.AsSpan(0, want));
                if (read == 0) { _innerExhausted = true; return; }
                Append(scratch.AsSpan(0, read));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (NeedsSpill(data.Length)) Spill();
        _store!.Position = _buffered;
        _store.Write(data);
        _buffered += data.Length;
    }

    private async ValueTask AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (NeedsSpill(data.Length)) await SpillAsync(ct).ConfigureAwait(false);
        _store!.Position = _buffered;
        await _store.WriteAsync(data, ct).ConfigureAwait(false);
        _buffered += data.Length;
    }

    private bool NeedsSpill(int incoming)
        => _store is MemoryStream && _buffered + incoming > _memoryThreshold;

    // Moves what is retained to a temp file. Deliberately does not touch the source: the frontier
    // stays where it is and later reads keep extending the file lazily.
    private void Spill()
    {
        var memory = (MemoryStream)_store!;
        var file   = CreateSpillFile();
        memory.Position = 0;
        memory.CopyTo(file);
        memory.Dispose();
        _store = file;
        SpilledToDisk?.Invoke(this, _spillPath!);
    }

    private async ValueTask SpillAsync(CancellationToken ct)
    {
        var memory = (MemoryStream)_store!;
        var file   = CreateSpillFile();
        memory.Position = 0;
        await memory.CopyToAsync(file, ct).ConfigureAwait(false);
        await memory.DisposeAsync().ConfigureAwait(false);
        _store = file;
        SpilledToDisk?.Invoke(this, _spillPath!);
    }

    // Guid-named rather than Path.GetTempFileName(), which creates the file up front and caps out at
    // 65535 names per temp directory - matching how the Azure node names its own staging files.
    private FileStream CreateSpillFile()
    {
        _spillPath = Path.Combine(Path.GetTempPath(), "vfs-seek-" + Guid.NewGuid().ToString("N"));
        return new FileStream(
            _spillPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 8192, FileOptions.DeleteOnClose);
    }

    // -- Disposal --------------------------------------------------------------

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _store?.Dispose();
            _store = null;
            if (_ownsInner) _inner.Dispose();
            DeleteSpillFile();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_store is not null) await _store.DisposeAsync().ConfigureAwait(false);
            _store = null;
            if (_ownsInner) await _inner.DisposeAsync().ConfigureAwait(false);
            DeleteSpillFile();
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    // FileOptions.DeleteOnClose has already removed the file in the ordinary case; this is the
    // backstop for the handle having been lost some other way.
    private void DeleteSpillFile()
    {
        if (_spillPath is null) return;
        try { File.Delete(_spillPath); } catch { /* best effort - DeleteOnClose is the primary path */ }
        _spillPath = null;
    }
}
