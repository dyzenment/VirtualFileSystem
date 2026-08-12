using System.Runtime.CompilerServices;

namespace Dytools.VirtualFileSystem.Benchmarks;

// Benchmark-only node designed to contribute the minimum possible overhead so
// benchmark results reflect the engine (pipeline, path, context) rather than
// node logic.
//
// - OpenReadAsync: new MemoryStream over a pre-allocated static buffer.
//   A fresh MemoryStream per call is unavoidable (streams have position state),
//   but wrapping a static byte[] means zero copy and zero inner allocation.
// - OpenWriteAsync: returns Stream.Null singleton - no allocation, discards bytes.
// - All other ops: return pre-cached Task instances or Task.CompletedTask.
// - ListAsync: yields nothing - measures the list pipeline with minimal iteration.
internal sealed class NullNode : VfsNodeBase
{
    // Shared read payload - MemoryStream wraps this without copying.
    private static readonly byte[] _payload = new byte[256];

    // Pre-cached Tasks for ops where the return value is a fixed singleton.
    private static readonly Task<Stream>      _writeTask = Task.FromResult<Stream>(Stream.Null);
    private static readonly Task<VfsNodeInfo?> _infoTask  = Task.FromResult<VfsNodeInfo?>(new VfsNodeInfo
    {
        RelativePath = VfsPath.From("file.dat"),
        IsFile       = true,
        IsDirectory  = false,
        SizeBytes    = 256,
        ModifiedAt   = DateTimeOffset.UtcNow,
    });

    // Task.FromResult(true) is cached by the .NET runtime for booleans - no alloc.
    private static readonly Task<bool> _existsTask = Task.FromResult(true);

    public override Task<Stream?> OpenReadAsync(VfsNodeRequest req, CancellationToken ct = default)
        => Task.FromResult<Stream?>(new MemoryStream(_payload, writable: false));

    public override Task<Stream> OpenWriteAsync(VfsNodeRequest req, VfsWriteMode mode = VfsWriteMode.Create, CancellationToken ct = default)
        => _writeTask;

    public override Task<bool> ExistsAsync(VfsNodeRequest req, CancellationToken ct = default)
        => _existsTask;

    public override Task<VfsNodeInfo?> GetInfoAsync(VfsNodeRequest req, CancellationToken ct = default)
        => _infoTask;

    public override Task DeleteAsync(VfsNodeRequest req, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task CopyAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task MoveAsync(VfsNodeRequest src, VfsNodeRequest dst, CancellationToken ct = default)
        => Task.CompletedTask;

    public override async IAsyncEnumerable<VfsNodeInfo> ListAsync(
        VfsNodeRequest req, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield break;
    }
}
