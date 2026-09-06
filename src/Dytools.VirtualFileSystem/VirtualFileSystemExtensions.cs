using System.Text;
using System.Text.Json;

namespace Dytools.VirtualFileSystem;

/// <summary>
/// Convenience built on the <see cref="IVirtualFileSystem"/> primitives - streams, text, bytes and JSON.
/// Deliberately extensions rather than interface members: none of it is something a node can do
/// better than the layer above, so it stays out of the contract every implementation has to honour.
/// <para>
/// Each of these opens a stream and disposes it before returning, so the commit-on-dispose behaviour
/// described on <see cref="IVirtualFileSystem.OpenWriteAsync"/> is already taken care of.
/// </para>
/// </summary>
public static class VirtualFileSystemExtensions
{
    // Encoding.UTF8 emits a byte-order mark, so writing through it silently prepends three bytes that
    // nobody asked for - invisible on read-back, since StreamReader strips it again, but very visible
    // to anything hashing or byte-comparing the file. Default to UTF-8 without one.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // -- Streams ---------------------------------------------------------------

    /// <summary>
    /// Writes everything readable from <paramref name="content"/> as the entry's content, then closes
    /// the VFS stream - which is what commits the write on the staging backends. The source stream is
    /// read to its end from its current position and is left open for the caller to dispose.
    /// <code>
    ///   await using var source = newDocument.GetStream();
    ///   await vfs.WriteAsync("/docs/report.pdf", source);
    /// </code>
    /// <para>
    /// <paramref name="options"/> defaults to <see cref="VfsWriteOptions.Default"/> (overwrite). A bare
    /// <see cref="VfsWriteMode"/> converts implicitly, so
    /// <c>WriteAsync(path, source, VfsWriteMode.Append)</c> binds; pass full
    /// <see cref="VfsWriteOptions"/> to also request timestamps on the written entry.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    public static async Task WriteAsync(
        this IVirtualFileSystem vfs, string path, Stream content,
        VfsWriteOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var stream = await vfs.OpenWriteAsync(path, options, ct);
        await content.CopyToAsync(stream, ct);
    }

    /// <summary>
    /// Reads the entry into <paramref name="destination"/> and returns whether it existed - false
    /// leaves <paramref name="destination"/> untouched. The counterpart to
    /// <see cref="WriteAsync(IVirtualFileSystem, string, Stream, VfsWriteOptions?, CancellationToken)"/>
    /// for callers that already hold the stream they want the bytes in.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
    public static async Task<bool> ReadToAsync(
        this IVirtualFileSystem vfs, string path, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await using var stream = await vfs.OpenReadAsync(path, ct);
        if (stream is null) return false;
        await stream.CopyToAsync(destination, ct);
        return true;
    }

    // -- Text ------------------------------------------------------------------

    /// <summary>Reads the whole entry as text, or null when it does not exist.</summary>
    public static async Task<string?> ReadAsStringAsync(
        this IVirtualFileSystem vfs, string path, Encoding? encoding = null, CancellationToken ct = default)
    {
        await using var stream = await vfs.OpenReadAsync(path, ct);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, encoding ?? Utf8NoBom);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Writes <paramref name="content"/> as the entry's whole content.
    /// <paramref name="options"/> defaults to <see cref="VfsWriteOptions.Default"/> (overwrite); pass
    /// <see cref="VfsWriteMode.Append"/> to add to what is already there.
    /// </summary>
    public static async Task WriteStringAsync(
        this IVirtualFileSystem vfs, string path, string content,
        Encoding? encoding = null, VfsWriteOptions? options = null, CancellationToken ct = default)
    {
        await using var stream = await vfs.OpenWriteAsync(path, options, ct);
        // Flushed before the VFS stream is disposed - disposing it is what commits the write.
        await using var writer = new StreamWriter(stream, encoding ?? Utf8NoBom, leaveOpen: true);
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

    /// <summary>
    /// Writes <paramref name="bytes"/> as the entry's whole content.
    /// <paramref name="options"/> defaults to <see cref="VfsWriteOptions.Default"/> (overwrite); pass
    /// <see cref="VfsWriteMode.Append"/> to add to what is already there.
    /// </summary>
    public static async Task WriteAllBytesAsync(
        this IVirtualFileSystem vfs, string path, ReadOnlyMemory<byte> bytes,
        VfsWriteOptions? options = null, CancellationToken ct = default)
    {
        await using var stream = await vfs.OpenWriteAsync(path, options, ct);
        await stream.WriteAsync(bytes, ct);
    }

    // -- JSON ------------------------------------------------------------------

    /// <summary>
    /// Serialises <paramref name="value"/> to JSON and writes it as the entry's content.
    /// <paramref name="options"/> defaults to <see cref="VfsWriteOptions.Default"/> (overwrite) and is
    /// the VFS write mode and timestamps, distinct from <paramref name="jsonOptions"/>.
    /// </summary>
    public static async Task SendAsync<T>(
        this IVirtualFileSystem vfs, string path, T value,
        JsonSerializerOptions? jsonOptions = null, VfsWriteOptions? options = null,
        CancellationToken ct = default)
    {
        await using var stream = await vfs.OpenWriteAsync(path, options, ct);
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
