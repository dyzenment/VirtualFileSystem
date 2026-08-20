using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dytools.VirtualFileSystem.Catalog;

/// <summary>
/// Serializes a <see cref="VfsPath"/> as its canonical string form (base + stream + query). Used by
/// <see cref="JsonFileVfsCatalog"/>; reusable by any <see cref="IVfsCatalog"/> implementation that serializes entries.
/// </summary>
public sealed class VfsPathJsonConverter : JsonConverter<VfsPath>
{
    /// <summary>Reads a <see cref="VfsPath"/> from its canonical string form.</summary>
    public override VfsPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => VfsPath.From(reader.GetString() ?? "");

    /// <summary>Writes a <see cref="VfsPath"/> as its canonical string form.</summary>
    public override void Write(Utf8JsonWriter writer, VfsPath value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
