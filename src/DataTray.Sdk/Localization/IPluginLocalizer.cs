namespace DataTray.Sdk.Localization;

/// <summary>
/// A plugin's view onto localization — the same shape as the host's own localizer, but backed by the
/// plugin's embedded <c>Lang/strings*.json</c> files rather than the host's resources. A plugin reaches it
/// through <see cref="Tools.ToolExecutionContext.Localizer"/> for runtime text (errors, progress), and the
/// host consults it to resolve a field/title's <c>*Key</c> against the current culture. Lookups fall back
/// culture → neutral language → the plugin's neutral/English base, and finally to the key itself, so a
/// missing translation degrades softly and never crashes.
/// </summary>
public interface IPluginLocalizer
{
    /// <summary>The string for <paramref name="key"/> in the current culture, or the key itself when no
    /// translation exists anywhere (visible last resort, same as the host localizer).</summary>
    string this[string key] { get; }

    /// <summary>Whether a real translation exists for <paramref name="key"/> (any culture in the fallback
    /// chain). Used by the host to decide between a plugin's <c>*Key</c> translation and its hardcoded
    /// fallback string.</summary>
    bool Contains(string key);

    /// <summary><see cref="this"/> with <see cref="string.Format(string, object?[])"/> placeholders.</summary>
    string Get(string key, params object[] args);
}
