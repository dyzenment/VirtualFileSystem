using System.Text;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class SeekableReadStreamTests
{
    // Stands in for an HTTP response body: forward-only, no length, and short reads.
    private sealed class Wire(byte[] data, int maxRead = 7) : Stream
    {
        private readonly MemoryStream _data = new(data);
        public int SyncReads;
        public int AsyncReads;
        public long BytesPulled;

        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v)          => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

        public override int Read(byte[] b, int o, int c) => Read(b.AsSpan(o, c));

        public override int Read(Span<byte> destination)
        {
            SyncReads++;
            var n = _data.Read(destination[..Math.Min(destination.Length, maxRead)]);
            BytesPulled += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
        {
            AsyncReads++;
            var n = _data.Read(destination.Span[..Math.Min(destination.Length, maxRead)]);
            BytesPulled += n;
            return ValueTask.FromResult(n);
        }
    }

    private static readonly byte[] Alphabet = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRSTUVWXYZ");

    private static string ReadExactly(Stream s, int count)
    {
        var buf = new byte[count];
        var total = 0;
        int n;
        while (total < count && (n = s.Read(buf, total, count - total)) > 0) total += n;
        return Encoding.ASCII.GetString(buf, 0, total);
    }

    // -- Position is the single authority -------------------------------------
    //
    // Each of these was a silent wrong-bytes bug in the original: reading Length, or seeking from
    // End, switched the stream onto a backing store whose own position had drifted from the
    // wrapper's. The store's position is now set immediately before every read and trusted no
    // further than that.

    [Fact]
    public void Seek_ThenLength_ThenRead_ServesFromTheSeekPosition()
    {
        using var s = new SeekableReadStream(new Wire(Alphabet));
        ReadExactly(s, 10);
        s.Seek(2, SeekOrigin.Begin);

        _ = s.Length;   // fills the buffer to the end behind the scenes

        Assert.Equal("CDEFG", ReadExactly(s, 5));
    }

    [Fact]
    public void Seek_FromEnd_OnAForwardOnlySource()
    {
        using var s = new SeekableReadStream(new Wire(Alphabet));

        s.Seek(-3, SeekOrigin.End);

        Assert.Equal(23, s.Position);
        Assert.Equal("XYZ", ReadExactly(s, 3));
    }

    [Fact]
    public void Seek_Backwards_AfterReadingForward()
    {
        using var s = new SeekableReadStream(new Wire(Alphabet));
        ReadExactly(s, 10);

        s.Position = 0;

        Assert.Equal("ABCDE", ReadExactly(s, 5));
        Assert.Equal(5, s.Position);
    }

    [Fact]
    public void Seek_Forward_PastWhatHasBeenRead()
    {
        using var s = new SeekableReadStream(new Wire(Alphabet));
        ReadExactly(s, 3);

        s.Seek(20, SeekOrigin.Begin);

        Assert.Equal("UVW", ReadExactly(s, 3));
    }

    [Fact]
    public void Read_ToTheEnd_ReturnsEverythingOnce()
    {
        using var s = new SeekableReadStream(new Wire(Alphabet));
        Assert.Equal(Encoding.ASCII.GetString(Alphabet), ReadExactly(s, Alphabet.Length));
        Assert.Equal(0, s.Read(new byte[4], 0, 4));
    }

    [Fact]
    public void Seek_ToNegativePosition_Throws()
    {
        using var s = new SeekableReadStream(new Wire(Alphabet));
        Assert.Throws<IOException>(() => s.Seek(-1, SeekOrigin.Begin));
    }

    // -- Pass-through ----------------------------------------------------------

    [Fact]
    public void AlreadySeekableSource_IsPassedThrough_AtItsOwnPosition()
    {
        var inner = new MemoryStream(Alphabet) { Position = 5 };
        using var s = new SeekableReadStream(inner);

        Assert.True(s.IsPassThrough);
        Assert.Equal(5, s.Position);          // reported 0 before
        Assert.Equal("FGH", ReadExactly(s, 3));
        Assert.Equal(8, s.Position);          // reported 3 before
        Assert.Equal(26, s.Length);
    }

    [Fact]
    public void AlreadySeekableSource_NeverSpills()
    {
        using var s = new SeekableReadStream(new MemoryStream(Alphabet), memoryThreshold: 0);
        ReadExactly(s, 26);
        Assert.False(s.HasSpilledToDisk);
    }

    // -- Laziness --------------------------------------------------------------

    [Fact]
    public void CrossingTheThreshold_DoesNotDrainTheSource()
    {
        var wire = new Wire(new byte[8 * 1024 * 1024], maxRead: 64 * 1024);
        using var s = new SeekableReadStream(wire, memoryThreshold: 1024 * 1024);

        var buffer = new byte[64 * 1024];
        long consumed = 0;
        while (consumed < 2 * 1024 * 1024)
        {
            var n = s.Read(buffer, 0, buffer.Length);
            if (n == 0) break;
            consumed += n;
        }

        Assert.True(s.HasSpilledToDisk);
        // The original copied the whole source to disk the moment the threshold was crossed.
        Assert.Equal(consumed, wire.BytesPulled);
    }

    [Fact]
    public void Spilling_KeepsTheContentIntact_AndStaysSeekable()
    {
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);

        using var s = new SeekableReadStream(new Wire(payload, maxRead: 4096), memoryThreshold: 8 * 1024);
        var forward = new byte[payload.Length];
        var total = 0;
        int n;
        while (total < payload.Length && (n = s.Read(forward, total, payload.Length - total)) > 0) total += n;

        Assert.True(s.HasSpilledToDisk);
        Assert.Equal(payload, forward);

        s.Position = 1000;
        var window = new byte[100];
        var got = 0;
        while (got < 100 && (n = s.Read(window, got, 100 - got)) > 0) got += n;
        Assert.Equal(payload[1000..1100], window);
    }

    [Fact]
    public void SpillFile_IsDeletedOnDispose()
    {
        string? spillPath = null;
        var s = new SeekableReadStream(new Wire(new byte[64 * 1024], maxRead: 4096), memoryThreshold: 8 * 1024);
        s.SpilledToDisk += (_, path) => spillPath = path;

        ReadExactly(s, 64 * 1024);
        Assert.NotNull(spillPath);
        Assert.True(File.Exists(spillPath));

        s.Dispose();
        Assert.False(File.Exists(spillPath));
    }

    // -- Async -----------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_UsesTheSourcesAsyncPath()
    {
        var wire = new Wire(Alphabet);
        await using var s = new SeekableReadStream(wire);

        var read = await s.ReadAsync(new byte[10].AsMemory());
        Assert.True(read > 0);

        // The original inherited Stream's fallback, which blocks on the synchronous Read - on an
        // HTTP body that is a blocking socket call inside the async pipeline.
        Assert.Equal(0, wire.SyncReads);
        Assert.True(wire.AsyncReads > 0);
    }

    [Fact]
    public async Task ReadAsync_SeeksAndSpillsTheSameWayAsRead()
    {
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);

        await using var s = new SeekableReadStream(
            new Wire(payload, maxRead: 4096), memoryThreshold: 8 * 1024);

        var sink = new byte[payload.Length];
        var total = 0;
        int n;
        while (total < payload.Length && (n = await s.ReadAsync(sink.AsMemory(total))) > 0) total += n;
        Assert.Equal(payload, sink);

        s.Position = 5000;
        var window = new byte[64];
        var got = 0;
        while (got < 64 && (n = await s.ReadAsync(window.AsMemory(got))) > 0) got += n;
        Assert.Equal(payload[5000..5064], window);
    }

    // -- Ownership and disposal ------------------------------------------------

    [Fact]
    public void OwnsInner_False_LeavesTheSourceOpen()
    {
        var inner = new MemoryStream(Alphabet);
        using (var s = new SeekableReadStream(inner)) { ReadExactly(s, 5); }
        Assert.True(inner.CanRead);
    }

    [Fact]
    public void OwnsInner_True_DisposesTheSource()
    {
        var inner = new MemoryStream(Alphabet);
        using (var s = new SeekableReadStream(inner, ownsInner: true)) { ReadExactly(s, 5); }
        Assert.False(inner.CanRead);
    }

    [Fact]
    public void Disposed_ThrowsOnUse()
    {
        var s = new SeekableReadStream(new Wire(Alphabet));
        s.Dispose();
        Assert.Throws<ObjectDisposedException>(() => s.Position);
        Assert.Throws<ObjectDisposedException>(() => s.Read(new byte[1], 0, 1));
    }

    [Fact]
    public void WriteSide_IsNotSupported()
    {
        using var s = new SeekableReadStream(new Wire(Alphabet));
        Assert.False(s.CanWrite);
        Assert.Throws<NotSupportedException>(() => s.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => s.SetLength(1));
    }

    [Fact]
    public void NullSource_Throws()
        => Assert.Throws<ArgumentNullException>(() => new SeekableReadStream(null!));
}
