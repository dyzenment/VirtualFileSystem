using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Extensions;

namespace Dytools.VirtualFileSystem.Benchmarks;

// Two benchmark classes with a shared [CategoryFilter] so you can run one or both:
//   dotnet run -c Release -- --filter 'Dytools.VirtualFileSystem.Benchmarks.VfsBenchmarks*'
//   dotnet run -c Release -- --filter 'Dytools.VirtualFileSystem.Benchmarks.BaselineBenchmarks*'
//
// Reading the results:
//   BaselineBenchmarks show the irreducible cost of the .NET primitives NullNode
//   returns (MemoryStream, cached Task, etc.) and the cost of bypassing the VFS
//   engine entirely by calling NullNode directly.
//
//   VfsBenchmarks - BaselineBenchmarks(matching op) = pure engine cost
//   (path normalisation + VfsContext + registry lookup + pipeline dispatch).

// -- Engine benchmarks ---------------------------------------------------------
//
// "SameNode"  = both paths resolve to the same NullNode instance (/a → nodeA).
// "CrossNode" = src on /a (nodeA), dst on /b (nodeB); pipeline falls back to
//               stream copy since nodes are different instances.
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class VfsBenchmarks
{
    private IVirtualFileSystem _vfs = null!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddVirtualFileSystem()
            .Mount("/a", new NullNode())
            .Mount("/b", new NullNode());
        var sp = services.BuildServiceProvider();
        sp.InitializeVirtualFileSystem();
        _vfs = sp.GetRequiredService<IVirtualFileSystem>();
    }

    // -- Read ------------------------------------------------------------------

    [Benchmark]
    public async Task Read()
    {
        await using var s = await _vfs.OpenReadAsync("/a/file.dat");
    }

    // -- Write -----------------------------------------------------------------

    [Benchmark]
    public async Task Write()
    {
        await using var s = await _vfs.OpenWriteAsync("/a/file.dat");
    }

    // -- Exists ----------------------------------------------------------------

    [Benchmark]
    public Task<bool> Exists() => _vfs.ExistsAsync("/a/file.dat");

    // -- GetInfo ---------------------------------------------------------------

    [Benchmark]
    public Task<VfsEntryInfo?> GetInfo() => _vfs.GetInfoAsync("/a/file.dat");

    // -- Delete ----------------------------------------------------------------

    [Benchmark]
    public Task Delete() => _vfs.DeleteAsync("/a/file.dat");

    // -- Copy ------------------------------------------------------------------

    // Same-node: node handles the copy natively (NullNode: Task.CompletedTask).
    [Benchmark]
    public Task Copy_SameNode() => _vfs.CopyAsync("/a/src.dat", "/a/dst.dat");

    // Cross-node: pipeline streams bytes from src to dst (read + CopyToAsync + write).
    [Benchmark]
    public Task Copy_CrossNode() => _vfs.CopyAsync("/a/src.dat", "/b/dst.dat");

    // -- Move ------------------------------------------------------------------

    [Benchmark]
    public Task Move_SameNode() => _vfs.MoveAsync("/a/src.dat", "/a/dst.dat");

    [Benchmark]
    public Task Move_CrossNode() => _vfs.MoveAsync("/a/src.dat", "/b/dst.dat");

    // -- Rename ----------------------------------------------------------------

    // Rename always stays in the same directory (same-node by construction).
    [Benchmark]
    public Task Rename() => _vfs.RenameAsync("/a/src.dat", "dst.dat");

    // -- List ------------------------------------------------------------------

    // NullNode yields nothing - measures pipeline + IAsyncEnumerable setup cost.
    [Benchmark]
    public async Task List()
    {
        await foreach (var _ in _vfs.ListAsync("/a")) { }
    }

    // -- Path normalisation (isolated) -----------------------------------------

    // Benchmarks a path with multiple segments to exercise the Span<char>
    // normalisation code independently of node dispatch.
    [Benchmark]
    public Task<bool> Exists_DeepPath() => _vfs.ExistsAsync("/a/x/y/z/../../file.dat");
}

// -- Baseline benchmarks -------------------------------------------------------
//
// Three layers of baselines:
//
//   Layer 1 - Raw .NET primitives
//     What does allocating a MemoryStream or a Task cost on its own?
//     This is the floor the NullNode operates at.
//
//   Layer 2 - Direct NullNode call (no VFS engine)
//     Calls the node's method directly with a hand-built VfsNodeRequest struct.
//     No path normalisation, no VfsContext, no registry, no pipeline.
//     VfsNodeRequest is a value type - zero heap allocation from the request itself.
//
//   Layer 3 - Direct node call for two-context operations
//     Copy/Move/Rename build two contexts - their baseline shows the inherent
//     dual-context cost before the engine wraps it.
//
// Engine overhead for a given operation ≈ VfsBenchmarks - matching Baseline.
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class BaselineBenchmarks
{
    // Shared payload - same static array NullNode uses.
    private static readonly byte[] _payload = new byte[256];

    // Pre-built request structs - value types, allocated on the stack at call site.
    // Path is the relative VfsPath - WithOffset(3) skips the "/a/" mount prefix so
    // PathSpan = "file.dat", leaving StreamSpan/QuerySpan intact.
    private static readonly VfsPath _mountA = VfsPath.From("/a");
    private static readonly VfsNodeRequest _req    = new(VfsPath.From("/a/file.dat").WithOffset(3), _mountA);
    private static readonly VfsNodeRequest _reqSrc = new(VfsPath.From("/a/src.dat") .WithOffset(3), _mountA);
    private static readonly VfsNodeRequest _reqDst = new(VfsPath.From("/a/dst.dat") .WithOffset(3), _mountA);

    private readonly NullNode _node = new();

    // -- Layer 1: raw .NET primitives ------------------------------------------

    // Floor for any Read result - allocates MemoryStream + Task wrapper.
    [Benchmark]
    public async Task Primitive_StreamAlloc()
    {
        await using var s = new MemoryStream(_payload, writable: false);
    }

    // Task.FromResult(true) is cached by the runtime for booleans - no Task alloc.
    // This measures the await overhead only.
    [Benchmark]
    public Task<bool> Primitive_CachedBoolTask() => Task.FromResult(true);

    // Task.CompletedTask is a singleton - floor for void async ops.
    [Benchmark]
    public Task Primitive_CompletedTask() => Task.CompletedTask;

    // -- Layer 2: direct NullNode call -----------------------------------------

    // What a Read costs with ZERO engine overhead:
    //   Task.FromResult<Stream?>(new MemoryStream(payload)) + await + dispose
    [Benchmark]
    public async Task Direct_Read()
    {
        await using var s = await _node.OpenReadAsync(_req);
    }

    // What a Write costs with ZERO engine overhead:
    //   cached Task<Stream>(Stream.Null) + await + dispose (Stream.Null.Dispose = no-op)
    [Benchmark]
    public async Task Direct_Write()
    {
        await using var s = await _node.OpenWriteAsync(_req);
    }

    // Exists: Task.FromResult(true) is runtime-cached - measures only await cost.
    [Benchmark]
    public Task<bool> Direct_Exists() => _node.ExistsAsync(_req);

    // GetInfo: cached Task<VfsNodeInfo?> - measures only await cost.
    [Benchmark]
    public Task<VfsNodeInfo?> Direct_GetInfo() => _node.GetInfoAsync(_req);

    // Delete: Task.CompletedTask - measures only await cost.
    [Benchmark]
    public Task Direct_Delete() => _node.DeleteAsync(_req);

    // -- Layer 3: direct node call, two-context operations ---------------------

    // Same-node copy: NullNode.CopyAsync = Task.CompletedTask.
    [Benchmark]
    public Task Direct_Copy_SameNode() => _node.CopyAsync(_reqSrc, _reqDst);

    // Same-node move: NullNode.MoveAsync = Task.CompletedTask.
    [Benchmark]
    public Task Direct_Move_SameNode() => _node.MoveAsync(_reqSrc, _reqDst);
}
