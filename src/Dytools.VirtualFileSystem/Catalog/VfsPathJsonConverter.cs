using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dytools.VirtualFileSystem.Catalog;

// Serializes a VfsPath as its canonical string form (base + stream + query). Used by
// JsonFileVfsCatalog; reusable by any IVfsCatalog implementation that serializes entries.
public sealed class VfsPathJsonConverter : JsonConverter<VfsPath>
{
    public override VfsPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => VfsPath.From(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, VfsPath value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
