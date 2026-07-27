namespace DataTray.Sdk.Localization;

public static class PluginLocalizerExtensions
{
    /// <summary>The rule behind every <c>*Key</c>: when <paramref name="key"/> is set and the plugin ships a
    /// matching translation, return it; otherwise return the hardcoded <paramref name="fallback"/> string.
    /// So a plugin that sets a key gets its translation (with the plugin's own English base as the middle
    /// fallback), and a plugin that sets none renders exactly as before.</summary>
    public static string Resolve(this IPluginLocalizer localizer, string? key, string fallback) =>
        key is { Length: > 0 } && localizer.Contains(key) ? localizer[key] : fallback;
}
