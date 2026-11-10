using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DataTray.Extensions.QueryBuilder.Model;

public static class QueryModelJson
{
    // Written on the root so a later format can recognise older files; a file without it is version 1.
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(Statement statement)
    {
        var root = JsonSerializer.SerializeToNode(statement, Options)?.AsObject() ?? new JsonObject();
        root.Insert(0, "formatVersion", FormatVersion);
        return root.ToJsonString(Options);
    }

    public static Statement Deserialize(string json) => JsonSerializer.Deserialize<Statement>(json, Options)!;
}
