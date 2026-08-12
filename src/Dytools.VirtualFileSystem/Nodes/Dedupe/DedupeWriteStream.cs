namespace Dytools.VirtualFileSystem.Nodes.Dedupe;

// Buffers a write to a temp file (pre-seeded with existing content for append), then
// on dispose hands it to the node to hash, store as a content-addressed blob, and
// record in the catalog. Content-addressing needs the full content before the key is
// known, so writes are always buffered.
internal sealed class DedupeWriteStream(
    DedupeNode node, string path, FileStream temp, DateTimeOffset createdAt) : Stream
{
    private bool _committed;

    public override bool CanWrite => true;
    public override bool CanRead  => false;
    public override bool CanSeek  => false;
    public override long Length   => temp.Length;
    public override long Position { get => temp.Position; set => throw new NotSupportedException(); }

    public override void      Write(byte[] b, int o, int c)                          => temp.Write(b, o, c);
    public override Task      WriteAsync(byte[] b, int o, int c, CancellationToken ct) => temp.WriteAsync(b, o, c, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => temp.WriteAsync(b, ct);
    public override void      Flush() => temp.Flush();

    public override int  Read(byte[] b, int o, int c)   => throw new NotSupportedException();
    public override long Seek(long o, SeekOrigin s)     => throw new NotSupportedException();
    public override void SetLength(long v)              => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        await CommitAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) CommitAsync().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    private async Task CommitAsync()
    {
        if (_committed) return;
        _committed = true;
        var tempPath = temp.Name;
        try
        {
            await node.CommitWriteAsync(path, temp, createdAt).ConfigureAwait(false);
        }
        finally
        {
            await temp.DisposeAsync().ConfigureAwait(false);
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
    }
}
