using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataTray.Extensions.QueryBuilder.Model;

internal sealed class ValueListConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ValueList<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(ValueListConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}
