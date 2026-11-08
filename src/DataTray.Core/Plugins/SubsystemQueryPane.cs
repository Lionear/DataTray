using DataTray.Sdk.Extensibility;
using DataTray.Sdk.Localization;

namespace DataTray.Core.Plugins;

/// <summary>A query-pane contributor with the identity and consent the host needs when building its context:
/// the declared capabilities decide per pane whether it gets schema access.</summary>
public sealed record SubsystemQueryPane(
    string PluginId,
    string PluginName,
    IReadOnlySet<string> Capabilities,
    IPluginLocalizer Localizer,
    IQueryPanePlugin Plugin);
