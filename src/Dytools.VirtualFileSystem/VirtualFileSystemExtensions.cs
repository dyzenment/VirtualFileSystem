using System.Text;
using System.Text.Json;

namespace Dytools.VirtualFileSystem;

/// <summary>
/// Convenience built on the <see cref="IVirtualFileSystem"/> primitives - text, bytes and JSON.
/// Deliberately extensions rather than interface members: none of it is something a node can do
/// better than the layer above, so it stays out of the contract every implementation has to honour.
/// <para>
/// Each of these opens a stream and disposes it before returning, so the commit-on-dispose behaviour
/// described on <see cref="IVirtualFileSystem.OpenWriteAsync"/> is already taken care of.
/// </para>
/// </summary>
public static class VirtualFileSystemExtensions
{
    // -- Text ------------------------------------------------------------------

    /// <summary>Reads the whole entry as text, or null when it does not exist.</summary>
    public static async Task<string?> ReadAsStringAsync(
        this IVirtualFileSystem vfs, string path, Encoding? encoding = null, CancellationToken ct = default)
    {
        await using var stream = await vfs.OpenReadAsync(path, ct);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, encoding ?? Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>Writes <paramref name="content"/> as the entry's whole content.</summary>
    public static async Task WriteStringAsync(
        this IVirtualFileSystem vfs, string path, string content,
        Encoding? encoding = null, CancellationToken ct = default)
    {
        await using var stream = await vfs.OpenWriteAsync(path, VfsWriteOptions.Default, ct);
        // Flushed before the VFS stream is disposed - disposing it is what commits the write.
        await using var writer = new StreamWriter(stream, encoding ?? Encoding.UTF8, leaveOpen: true);
        await writer.WriteAsync(content.AsMemory(), ct);
        await writer.FlushAsync(ct);
    }

    // -- Bytes -----------------------------------------------------------------

    /// <summary>Reads the whole entry as bytes, or null when it does not exist.</summary>
    public static async Task<byte[]?> ReadAllBytesAsync(
        this IVirtualFileSystem vfs, string path, CancellationToken ct = default)
    {
        await using var stream = await vfs.OpenReadAsync(path, ct);
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    /// <summary>Writes <paramref name="bytes"/> as the entry's whole content.</summary>
    public static async Task WriteAllBytesAsync(
        this IVirtualFileSystem vfs, string path, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        await using var stream = await vfs.OpenWriteAsync(path, VfsWriteOptions.Default, ct);
        await stream.WriteAsync(bytes, ct);
    }

    // -- JSON ------------------------------------------------------------------

    /// <summary>Serialises <paramref name="value"/> to JSON and writes it as the entry's content.</summary>
    public static async Task SendAsync<T>(
        this IVirtualFileSystem vfs, string path, T value,
        JsonSerializerOptions? jsonOptions = null, CancellationToken ct = default)
    {
        await using var stream = await vfs.OpenWriteAsync(path, VfsWriteOptions.Default, ct);
        await JsonSerializer.SerializeAsync(stream, value, jsonOptions, ct);
    }

    /// <summary>
    /// Reads the entry and deserialises it into <typeparamref name="T"/>, or the default when the
    /// entry does not exist. Pass the same <paramref name="jsonOptions"/> used to write it.
    /// </summary>
    public static async Task<T?> RetrieveAsync<T>(
        this IVirtualFileSystem vfs, string path,
        JsonSerializerOptions? jsonOptions = null, CancellationToken ct = default)
    {
        await using var stream = await vfs.OpenReadAsync(path, ct);
        if (stream is null) return default;
        return await JsonSerializer.DeserializeAsync<T>(stream, jsonOptions, ct);
    }
}
