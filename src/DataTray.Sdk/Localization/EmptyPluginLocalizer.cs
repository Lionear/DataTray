namespace DataTray.Sdk.Localization;

/// <summary>A no-op localizer for plugins that ship no translations: every lookup returns the key (or the
/// formatted key). The host hands this to a tool's context when the plugin declared no <c>localization</c>,
/// so <see cref="Tools.ToolExecutionContext.Localizer"/> is always non-null.</summary>
public sealed class EmptyPluginLocalizer : IPluginLocalizer
{
    public static readonly EmptyPluginLocalizer Instance = new();

    private EmptyPluginLocalizer()
    {
    }

    public string this[string key] => key;

    public bool Contains(string key) => false;

    public string Get(string key, params object[] args) => args.Length == 0 ? key : string.Format(key, args);
}
