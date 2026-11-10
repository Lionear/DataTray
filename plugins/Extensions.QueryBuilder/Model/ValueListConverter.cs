using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataTray.Extensions.QueryBuilder.Model;

internal sealed class ValueListConverter<T> : JsonConverter<ValueList<T>>
{
    public override ValueList<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        List<T>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<T>>(ref reader, options);
        }
        catch (JsonException e)
        {
            throw ModelJsonErrors.FromInsideList(e);
        }

        var list = items ?? throw new JsonException("Expected a list.");
        return list.Any(item => item is null) ? throw new JsonException("A list in the model contains null.") : new(list);
    }

    public override void Write(Utf8JsonWriter writer, ValueList<T> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToList(), options);
}
