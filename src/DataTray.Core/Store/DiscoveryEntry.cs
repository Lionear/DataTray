using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataTray.Core.Store;

/// <summary>
/// The <c>discovery.json</c> hosted by Lionear at a fixed, non-configurable URL. It is a registry of
/// <b>stores</b> (not itself a plugin index): each <see cref="DiscoveryEntry"/> points at a store's own
/// <c>index.json</c>. Every listed store is auto-merged into the catalog — there is no per-store opt-in
/// (trust is placed in Lionear's registration process before a store lands here). Versioned via
/// <see cref="SchemaVersion"/>.
/// </summary>
public sealed record DiscoveryDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("stores")]
    public IReadOnlyList<DiscoveryEntry> Stores { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static DiscoveryDocument Parse(string json) =>
        JsonSerializer.Deserialize<DiscoveryDocument>(json, Options)
        ?? throw new InvalidDataException("Discovery document deserialised to null.");
}

/// <summary>One registered store in the Discovery feed: enough metadata to show a source card without
/// first fetching its index. Its <see cref="IndexUrl"/> is auto-added to the catalog merge.</summary>
public sealed record DiscoveryEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; init; }

    /// <summary>The store's own <c>index.json</c> URL, merged into the catalog.</summary>
    [JsonPropertyName("indexUrl")]
    public required string IndexUrl { get; init; }

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; init; }

    [JsonPropertyName("homepage")]
    public string? Homepage { get; init; }
}
