using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataTray.Extensions.QueryBuilder.Model;

internal sealed class ValueListConverter<T> : JsonConverter<ValueList<T>>
{
    public override ValueList<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var items = JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? throw new JsonException("Expected a list.");
        return items.Any(item => item is null) ? throw new JsonException("A list in the model contains null.") : new(items);
    }

    public override void Write(Utf8JsonWriter writer, ValueList<T> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToList(), options);
}
