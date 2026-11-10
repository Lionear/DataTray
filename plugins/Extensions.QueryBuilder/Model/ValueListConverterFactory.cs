using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataTray.Extensions.QueryBuilder.Model;

internal sealed class ValueListConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ValueList<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        Activator.CreateInstance(typeof(ValueListConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0])) as JsonConverter
        ?? throw new InvalidOperationException($"No list converter could be created for {typeToConvert}.");
}
