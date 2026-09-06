using System.Text;
using Dytools.VirtualFileSystem.Nodes.LocalFs;

namespace Dytools.VirtualFileSystem.Tests;

public sealed class WriteExtensionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfs-writeext-" + Guid.NewGuid().ToString("N"));

    private IVirtualFileSystem CreateLocal()
    {
        Directory.CreateDirectory(_root);
        return VfsFactory.Build(b => b.Mount("/local", new LocalFsNode(_root)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static MemoryStream Source(string text) => new(Encoding.UTF8.GetBytes(text));

    // -- WriteAsync(stream) ----------------------------------------------------

    [Fact]
    public async Task WriteAsync_FromStream_StoresTheContent()
    {
        var vfs = VfsFactory.CreateDual();
        await using var source = Source("streamed payload");

        await vfs.WriteAsync("/a/doc.bin", source);

        Assert.Equal("streamed payload", await vfs.ReadAsStringAsync("/a/doc.bin"));
    }

    [Fact]
    public async Task WriteAsync_LeavesTheSourceStreamOpen()
    {
        var vfs = VfsFactory.CreateDual();
        await using var source = Source("payload");

        await vfs.WriteAsync("/a/doc.bin", source);

        // Disposing the caller's stream is the caller's business - touching it must still work.
        Assert.True(source.CanRead);
        Assert.Equal(source.Length, source.Position);
    }

    [Fact]
    public async Task WriteAsync_ReadsFromTheCurrentPosition()
    {
        var vfs = VfsFactory.CreateDual();
        await using var source = Source("skip-me:keep-me");
        source.Position = "skip-me:".Length;

        await vfs.WriteAsync("/a/doc.bin", source);

        Assert.Equal("keep-me", await vfs.ReadAsStringAsync("/a/doc.bin"));
    }

    [Fact]
    public async Task WriteAsync_Overwrites_ByDefault()
    {
        var vfs = VfsFactory.CreateDual();
        await vfs.WriteStringAsync("/a/doc.bin", "original");

        await using var source = Source("replacement");
        await vfs.WriteAsync("/a/doc.bin", source);

        Assert.Equal("replacement", await vfs.ReadAsStringAsync("/a/doc.bin"));
    }

    [Fact]
    public async Task WriteAsync_TakesABareWriteMode_ThroughTheImplicitConversion()
    {
        var vfs = VfsFactory.CreateDual();
        await vfs.WriteStringAsync("/a/log.txt", "first");

        await using var source = Source("-second");
        await vfs.WriteAsync("/a/log.txt", source, VfsWriteMode.Append);

        Assert.Equal("first-second", await vfs.ReadAsStringAsync("/a/log.txt"));
    }

    [Fact]
    public async Task WriteAsync_NullContent_Throws()
    {
        var vfs = VfsFactory.CreateDual();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => vfs.WriteAsync("/a/doc.bin", null!));
    }

    // -- ReadToAsync -----------------------------------------------------------

    [Fact]
    public async Task ReadToAsync_CopiesIntoTheDestination()
    {
        var vfs = VfsFactory.CreateDual();
        await vfs.WriteStringAsync("/a/doc.txt", "payload");

        using var destination = new MemoryStream();
        var found = await vfs.ReadToAsync("/a/doc.txt", destination);

        Assert.True(found);
        Assert.Equal("payload", Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Fact]
    public async Task ReadToAsync_MissingEntry_ReturnsFalseAndWritesNothing()
    {
        var vfs = VfsFactory.CreateDual();

        using var destination = new MemoryStream();
        var found = await vfs.ReadToAsync("/a/missing.txt", destination);

        Assert.False(found);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task ReadToAsync_NullDestination_Throws()
    {
        var vfs = VfsFactory.CreateDual();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => vfs.ReadToAsync("/a/doc.txt", null!));
    }

    // -- Write options on the text / bytes / JSON helpers -----------------------

    [Fact]
    public async Task WriteStringAsync_TakesAWriteMode()
    {
        var vfs = VfsFactory.CreateDual();
        await vfs.WriteStringAsync("/a/log.txt", "first");
        await vfs.WriteStringAsync("/a/log.txt", "-second", options: VfsWriteMode.Append);

        Assert.Equal("first-second", await vfs.ReadAsStringAsync("/a/log.txt"));
    }

    [Fact]
    public async Task WriteAllBytesAsync_TakesAWriteMode()
    {
        var vfs = VfsFactory.CreateDual();
        await vfs.WriteAllBytesAsync("/a/log.bin", new byte[] { 1, 2 });
        await vfs.WriteAllBytesAsync("/a/log.bin", new byte[] { 3 }, VfsWriteMode.Append);

        Assert.Equal(new byte[] { 1, 2, 3 }, await vfs.ReadAllBytesAsync("/a/log.bin"));
    }

    [Fact]
    public async Task SendAsync_TakesAWriteMode_SeparatelyFromTheJsonOptions()
    {
        var vfs = CreateLocal();
        var stamp = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        await vfs.SendAsync("/local/state.json", new { Count = 3 },
                            jsonOptions: null, options: new VfsWriteOptions { ModifiedAt = stamp });

        var info = await vfs.GetInfoAsync("/local/state.json");
        Assert.NotNull(info);
        Assert.Equal(stamp, info.ModifiedAt!.Value.ToUniversalTime());
    }

    // -- Create is a replace, not an in-place overwrite -------------------------
    //
    // The mode is named after FileMode.Create, so it is easy to read as "seek to 0 and write over",
    // which would leave a tail of the old content when the new content is shorter. It does not.

    [Fact]
    public async Task Create_OverwritingWithShorterContent_LeavesNoTail_InMemory()
    {
        var vfs = VfsFactory.CreateDual();
        await vfs.WriteStringAsync("/a/doc.txt", "a long original payload");

        await using var source = Source("short");
        await vfs.WriteAsync("/a/doc.txt", source);

        Assert.Equal("short", await vfs.ReadAsStringAsync("/a/doc.txt"));
    }

    [Fact]
    public async Task Create_OverwritingWithShorterContent_LeavesNoTail_LocalFs()
    {
        var vfs = CreateLocal();
        await vfs.WriteStringAsync("/local/doc.txt", "a long original payload");

        await vfs.WriteStringAsync("/local/doc.txt", "short");

        Assert.Equal("short", await vfs.ReadAsStringAsync("/local/doc.txt"));
        Assert.Equal(5, new FileInfo(Path.Combine(_root, "doc.txt")).Length);
    }
}
