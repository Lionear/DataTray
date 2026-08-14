using Avalonia.Media;

namespace DataTray.Sdk.Extensibility;

/// <summary>
/// One action a plugin adds to a query window's toolbar. <see cref="AppliesTo"/> decides per document
/// whether the button appears at all — the same shape <see cref="ConnectionMenuContribution"/> already uses
/// — so a plugin scopes itself to a mode
/// (<c>doc =&gt; doc.Kind == QueryDocumentKind.Query</c>), an engine
/// (<c>doc =&gt; doc.Connection?.ProviderId == "mssql"</c>), a database, or any combination, and the host
/// needs no per-plugin wiring. It is re-evaluated when the tab's connection, database or mode changes.
/// </summary>
/// <remarks>
/// There is no <c>DefaultGesture</c> here, unlike <see cref="ToolbarContribution"/>: the keymap has no
/// notion of document scope, so a gesture on this surface would need a scope concept that does not exist.
/// </remarks>
public sealed record QueryToolbarContribution(
    string Id,
    string Title,
    Func<IQueryDocument, bool> AppliesTo,
    Func<IQueryDocument, IHostUi, Task> InvokeAsync)
{
    /// <summary>Stroked vector geometry, owned by the plugin (see <see cref="ToolbarContribution.Icon"/>).</summary>
    public Geometry? Icon { get; init; }

    /// <summary>Tooltip; falls back to <see cref="Title"/> when null.</summary>
    public string? Tooltip { get; init; }
}

/// <summary>
/// Optional contribution a standing-subsystem plugin may implement to add buttons to the toolbar of each
/// query window it applies to. Gated by the <see cref="PluginCapabilities.Toolbar"/> capability, like
/// <see cref="IToolbarPlugin"/>.
/// </summary>
public interface IQueryToolbarPlugin
{
    /// <summary>The query-window toolbar actions this plugin contributes.</summary>
    IReadOnlyList<QueryToolbarContribution> QueryToolbarItems { get; }
}
