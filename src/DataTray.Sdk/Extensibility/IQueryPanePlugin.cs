using Avalonia.Controls;
using Avalonia.Media;

namespace DataTray.Sdk.Extensibility;

/// <summary>
/// Optional contribution a standing-subsystem plugin may implement to add a pane inside query tabs
/// (e.g. a visual query builder), gated by <see cref="PluginCapabilities.QueryPane"/>. The host owns the
/// pane's toggle, placement and lifetime; the plugin supplies the control.
/// </summary>
public interface IQueryPanePlugin
{
    /// <summary>Stable id of the pane, unique within the plugin.</summary>
    string PaneId { get; }

    /// <summary>Text of the pane's toggle.</summary>
    string Title { get; }

    /// <summary>Stroked vector icon for the toggle, owned by the plugin; null for none.</summary>
    Geometry? Icon => null;

    /// <summary>Optional shortcut such as <c>"Ctrl+Shift+B"</c>; the host binds it per query tab.</summary>
    string? Shortcut => null;

    /// <summary>Whether the pane is offered for this document; re-evaluated on <see cref="IQueryDocument.ContextChanged"/>.</summary>
    bool AppliesTo(IQueryDocument document);

    /// <summary>Builds the pane's control for one query tab. The host disposes it when the tab closes.</summary>
    Control CreatePane(IQueryPaneContext context);
}
