using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DataTray.Core.Localization;
using DataTray.Sdk.Localization;

namespace DataTray.Core.Plugins;

/// <summary>
/// The host-built <see cref="IPluginLocalizer"/> for one plugin assembly. Reads all
/// <c>{prefix}[.culture].json</c> files embedded in the plugin (same "embedded resource + graceful
/// fallback" precedent as the icon loader), keyed by culture — "" for the neutral/English base. Lookups
/// follow the live host culture down the .NET fallback chain (nl-NL → nl → neutral), then the key itself,
/// so a missing translation degrades softly. Reads the host culture on each lookup, so switching language
/// in Settings takes effect the next time a string is resolved.
/// </summary>
public sealed class PluginLocalizer : IPluginLocalizer
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false };

    private readonly ILocalizer _host;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _byCulture;

    private PluginLocalizer(ILocalizer host, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> byCulture)
    {
        _host = host;
        _byCulture = byCulture;
    }

    /// <summary>
    /// Build a localizer for <paramref name="assembly"/> using the manifest's resource <paramref name="prefix"/>
    /// (e.g. "Lang/strings"). Returns null when the prefix is set but no neutral base resource
    /// ({prefix}.json) is found — the loader logs a warning and the plugin falls back to hardcoded strings.
    /// </summary>
    public static PluginLocalizer? TryLoad(Assembly assembly, string prefix, ILocalizer host, Action<string> warn)
    {
        var byCulture = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var culture = CultureFromResource(resource, prefix);
            if (culture is null)
            {
                continue;
            }

            try
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, Options);
                if (map is not null)
                {
                    byCulture[culture] = map;
                }
            }
            catch (JsonException ex)
            {
                warn($"Plugin localization resource '{resource}' is not valid JSON: {ex.Message}");
            }
        }

        if (!byCulture.ContainsKey(string.Empty))
        {
            warn($"Plugin declares localization '{prefix}' but no neutral base resource '{prefix}.json' was found; using hardcoded strings.");
            return null;
        }

        return new PluginLocalizer(host, byCulture);
    }

    // "Lang/strings.json" → "" (neutral); "Lang/strings.nl.json" → "nl"; "Lang/strings.pt-BR.json" → "pt-BR".
    // Anything not matching the prefix (case-sensitive on the path) returns null and is skipped.
    private static string? CultureFromResource(string resource, string prefix)
    {
        const string ext = ".json";
        if (!resource.StartsWith(prefix, StringComparison.Ordinal) || !resource.EndsWith(ext, StringComparison.Ordinal))
        {
            return null;
        }

        var middle = resource[prefix.Length..^ext.Length]; // "" or ".nl" or ".pt-BR"
        if (middle.Length == 0)
        {
            return string.Empty;
        }

        return middle[0] == '.' ? middle[1..] : null; // require the '.' separator, else it's a different resource
    }

    public string this[string key] => Lookup(key) ?? key;

    public bool Contains(string key) => Lookup(key) is not null;

    public string Get(string key, params object[] args)
    {
        var format = this[key];
        return args.Length == 0 ? format : string.Format(CultureInfo.CurrentCulture, format, args);
    }

    private string? Lookup(string key)
    {
        foreach (var culture in FallbackChain(_host.Culture))
        {
            if (_byCulture.TryGetValue(culture, out var map) && map.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    // Live culture → its neutral language → the neutral/English base. e.g. nl-NL → nl → "".
    private static IEnumerable<string> FallbackChain(CultureInfo culture)
    {
        if (culture.Name.Length > 0)
        {
            yield return culture.Name;
        }

        var twoLetter = culture.TwoLetterISOLanguageName;
        if (twoLetter.Length > 0 && !string.Equals(twoLetter, culture.Name, StringComparison.OrdinalIgnoreCase))
        {
            yield return twoLetter;
        }

        yield return string.Empty;
    }
}
