namespace Dytools.VirtualFileSystem.Nodes.SharePoint;

// Buffers a write to a temp file, then on close hands it to the node to upload (single PUT for
// small items, chunked upload session for large ones). SharePoint has no streaming-append write,
// so we stage locally and commit atomically - the same pattern as the S3 and Azure nodes.
internal sealed class SharePointUploadStream : Stream
{
    private readonly SharePointNode _node;
    private readonly string         _drivePath;
    private readonly VfsWriteMode   _mode;
    private readonly string         _tempPath;
    private readonly FileStream     _temp;
    private          bool           _committed;

    public SharePointUploadStream(SharePointNode node, string drivePath, VfsWriteMode mode)
    {
        _node      = node;
        _drivePath = drivePath;
        _mode      = mode;
        _tempPath  = Path.Combine(Path.GetTempPath(), "vfs-sp-" + Guid.NewGuid().ToString("N"));
        _temp      = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.ReadWrite,
                                    FileShare.None, bufferSize: 4096, useAsync: true);
    }

    public override bool CanWrite => true;
    public override bool CanRead  => false;
    public override bool CanSeek  => false;
    public override long Length   => _temp.Length;
    public override long Position { get => _temp.Position; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => _temp.Write(buffer, offset, count);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => _temp.WriteAsync(buffer, offset, count, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => _temp.WriteAsync(buffer, ct);
    public override void Flush() => _temp.Flush();

    public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)       => throw new NotSupportedException();
    public override void SetLength(long value)                      => throw new NotSupportedException();

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
        try
        {
            await _node.CommitUploadAsync(_drivePath, _temp, _mode).ConfigureAwait(false);
        }
        finally
        {
            await _temp.DisposeAsync().ConfigureAwait(false);
            try { File.Delete(_tempPath); } catch { /* best-effort cleanup */ }
        }
    }
}
