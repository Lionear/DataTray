using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataTray.Extensions.QueryBuilder.Model;

internal sealed class ValueListConverter<T> : JsonConverter<ValueList<T>>
{
    public override ValueList<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize<List<T>>(ref reader, options)!);

    public override void Write(Utf8JsonWriter writer, ValueList<T> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToList(), options);
}
