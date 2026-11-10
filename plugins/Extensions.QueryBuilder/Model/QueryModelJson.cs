using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RequireNonNullableParameters } },
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };

    public static string Serialize(Statement statement)
    {
        var root = JsonSerializer.SerializeToNode(statement, Options)?.AsObject() ?? new JsonObject();
        root.Insert(0, "formatVersion", FormatVersion);
        return root.ToJsonString(Options);
    }

    /// <summary>Loads a saved model; bad content becomes the result's Error, never an exception.</summary>
    public static QueryModelLoadResult Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Failed("The saved query is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException e)
        {
            return Failed($"The saved query is not valid JSON: {e.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("formatVersion", out var versionElement))
            {
                if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version) || version <= 0)
                {
                    return Failed("The saved query has an unreadable format version.");
                }

                if (version > FormatVersion)
                {
                    return Failed($"The saved query was written by a newer version (format {version}).");
                }
            }

            try
            {
                return root.Deserialize<Statement>(Options) is { } statement
                    ? new QueryModelLoadResult(statement, null)
                    : Failed("The saved query is empty.");
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                return Failed($"The saved query does not match the query model: {ModelJsonErrors.Describe(e)}");
            }
        }
    }

    private static QueryModelLoadResult Failed(string error) => new(null, error);

    // Nullable parameters stay optional (Serialize omits nulls); every other constructor parameter must be present.
    private static void RequireNonNullableParameters(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.AssociatedParameter is { IsNullable: false })
            {
                property.IsRequired = true;
            }
        }
    }
}
