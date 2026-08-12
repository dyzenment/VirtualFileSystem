using Microsoft.Extensions.DependencyInjection;
using Dytools.VirtualFileSystem.Extensions;
using Dytools.VirtualFileSystem.Nodes.InMemory;

namespace Dytools.VirtualFileSystem.Tests;

// Shared factory helpers - each call builds a fresh, isolated VFS instance.
internal static class VfsFactory
{
    // Two named InMemory mounts: /a and /b on separate node instances.
    // Use this for all cross-node tests.
    public static IVirtualFileSystem CreateDual()
        => CreateDual(out _, out _);

    public static IVirtualFileSystem CreateDual(out InMemoryKvNode nodeA, out InMemoryKvNode nodeB)
    {
        var a = new InMemoryKvNode();
        var b = new InMemoryKvNode();
        nodeA = a;
        nodeB = b;
        return Build(vfsBuilder => vfsBuilder.Mount("/a", a).Mount("/b", b));
    }

    // Full control over the builder - used by alias and middleware tests.
    public static IVirtualFileSystem Build(Action<IVfsBuilder> configure)
    {
        var services = new ServiceCollection();
        var builder  = services.AddVirtualFileSystem();
        configure(builder);
        var sp = services.BuildServiceProvider();
        sp.InitializeVirtualFileSystem();
        return sp.GetRequiredService<IVirtualFileSystem>();
    }

    // -- Stream helpers --------------------------------------------------------

    public static async Task<string?> ReadTextAsync(IVirtualFileSystem vfs, string path)
    {
        await using var stream = await vfs.OpenReadAsync(path);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public static async Task WriteTextAsync(IVirtualFileSystem vfs, string path, string content)
    {
        await using var stream = await vfs.OpenWriteAsync(path);
        await using var writer = new StreamWriter(stream, leaveOpen: true);
        await writer.WriteAsync(content);
        await writer.FlushAsync();
    }

    public static async Task<byte[]?> ReadBytesAsync(IVirtualFileSystem vfs, string path)
    {
        await using var stream = await vfs.OpenReadAsync(path);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }
}
