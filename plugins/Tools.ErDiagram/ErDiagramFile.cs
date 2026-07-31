using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataTray.Tools.ErDiagram;

/// <summary>
/// A saved diagram (SE-225). It records <b>which diagram you composed</b> — the tables you picked — and
/// deliberately nothing about what is inside them.
/// </summary>
/// <remarks>
/// <para>No columns, no types, no constraints. That is the line that keeps this inside SE-82's Model A
/// decision: with no schema detail in the file there is no second version of the truth, so there is
/// nothing to diff, nothing to synchronise, and no question of which copy is right. Opening one reads the
/// <i>live</i> schema and draws the intersection.</para>
///
/// <para>It would be easy to also store each table's columns "so it opens faster". That trade is the whole
/// of Model B arriving by the back door: the file would start to age the moment anyone added a column,
/// and the answer to "which one is correct" would have to be invented.</para>
/// </remarks>
public sealed record ErDiagramFile
{
    public const string Extension = "dterd";

    /// <summary>Bumped only for a change old readers cannot survive. Unknown-but-newer is refused with a
    /// sentence rather than half-read.</summary>
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Which engine this was drawn against. Opening it on another is allowed — the same schema on
    /// an acceptance server is the obvious case — but a different provider is worth saying out loud.</summary>
    [JsonPropertyName("providerId")]
    public string ProviderId { get; init; } = "";

    /// <summary>The connection's display name when it was saved. Informational: identity is the file, not
    /// the connection, so nothing refuses to open because a connection was renamed.</summary>
    [JsonPropertyName("connectionName")]
    public string ConnectionName { get; init; } = "";

    [JsonPropertyName("database")]
    public string? Database { get; init; }

    /// <summary>Schema-qualified table keys, as <see cref="TableDef.Key"/> spells them.</summary>
    [JsonPropertyName("tables")]
    public IReadOnlyList<string> Tables { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a saved diagram. Throws <see cref="InvalidDataException"/> with something worth
    /// showing the user rather than letting a JSON exception reach the status line.</summary>
    public static ErDiagramFile FromJson(string json)
    {
        ErDiagramFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ErDiagramFile>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(ex.Message);
        }

        if (file is null)
        {
            throw new InvalidDataException("The file is empty.");
        }

        if (file.SchemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"This diagram was saved by a newer version of DataTray (format {file.SchemaVersion}).");
        }

        return file;
    }

    /// <summary>
    /// What a saved diagram means against a schema as it is now: the tables that are still there, and the
    /// ones that are not.
    /// </summary>
    /// <remarks>
    /// The missing ones are returned rather than quietly dropped. A table disappearing between saving a
    /// diagram and opening it is exactly the kind of thing the diagram is opened to find out, and a
    /// picture that silently draws eleven of your twelve tables is worse than no picture.
    /// </remarks>
    public ErResolvedFile ResolveAgainst(IReadOnlyList<TableDef> tables)
    {
        var available = tables
            .Select(t => t.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var present = Tables.Where(available.Contains).ToList();
        var missing = Tables.Where(t => !available.Contains(t)).ToList();

        return new ErResolvedFile(present, missing);
    }
}

/// <summary>A saved diagram matched against the live schema.</summary>
public sealed record ErResolvedFile(IReadOnlyList<string> Present, IReadOnlyList<string> Missing);
