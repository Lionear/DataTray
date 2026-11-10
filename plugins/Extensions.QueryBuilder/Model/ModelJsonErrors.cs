using System.Text.Json;

namespace DataTray.Extensions.QueryBuilder.Model;

// A list is read by a nested serializer call whose Path starts at the list. Rethrown without a Path, the outer
// serializer fills in the list's own location; the part inside the list travels in Data and is joined at the top.
internal static class ModelJsonErrors
{
    private const string InnerPathKey = "DataTray.QueryBuilder.InnerPath";

    private static readonly string ModelNamespace = typeof(Statement).Namespace + ".";

    public static JsonException FromInsideList(JsonException inner)
    {
        var relocated = new JsonException(Core(inner), inner);
        relocated.Data[InnerPathKey] = Inner(inner);
        return relocated;
    }

    /// <summary>The message with the absolute JSON path and short model type names, for the user-facing Error.</summary>
    public static string Describe(Exception error) =>
        error is JsonException { Path: { } path } json
            ? $"{Core(json)} (at {path}{json.Data[InnerPathKey] as string})"
            : Core(error);

    private static string Inner(JsonException error) => (error.Path ?? "$")[1..] + (error.Data[InnerPathKey] as string);

    // Drops the serializer's own " Path: … | LineNumber: …" suffix; Describe adds the absolute path instead.
    private static string Core(Exception error) =>
        error.Message.Split(" Path: ")[0].Replace(ModelNamespace, string.Empty, StringComparison.Ordinal);
}
