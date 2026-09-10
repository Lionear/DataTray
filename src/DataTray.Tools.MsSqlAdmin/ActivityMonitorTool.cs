using Avalonia.Controls;
using Avalonia.Media;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// SQL Server's Activity Monitor, as SSMS lays it out: the Overview strip of live graphs over Processes,
/// Resource Waits, Data File I/O and the two expensive-query grids, all collapsible and all refreshing on
/// one timer.
/// </summary>
/// <remarks>
/// <para>It opens as a <b>tab</b> (<see cref="IToolDocumentUi"/>, SE-216) rather than a dialog: a monitor
/// is something you leave open beside the query you are trying to explain. Because of that
/// <see cref="ExecuteAsync"/> is never called — opening the tab is the whole action — and
/// <see cref="Fields"/> stays empty.</para>
/// <para>It replaces the host's generic Activity Monitor for SQL Server (<c>MsSqlProvider</c> no longer
/// declares <c>SupportsActivityMonitor</c>): that one grid is this tab's Processes section with eight fewer
/// columns, and two entries called "Activity Monitor" on the same node would be a puzzle rather than a
/// choice. Postgres and MySQL keep the host's monitor exactly as it was — and behind the same menu item,
/// since <see cref="IsActivityMonitor"/> keeps this one where the built-in monitor has always been rather
/// than under the node's Tools submenu, where a tool would otherwise land.</para>
/// </remarks>
public sealed class ActivityMonitorTool : IToolPlugin, IToolDocumentUi
{
    public string Id => "mssql-activity-monitor";

    public string Title => "Activity Monitor";
    public string? TitleKey => "activity.title";

    public string DialogTitle => "Activity Monitor";
    public string? DialogTitleKey => "activity.title";

    public string? Description =>
        "Live server activity from the DMVs: processes, resource waits, file I/O and the most expensive "
        + "queries. Reads only — nothing is changed except by the Kill Process action, which asks first.";

    /// <summary>A whole-instance view, so it sits on the connection root and nowhere else.</summary>
    public ToolTarget Target { get; } = new(
        ProviderIds: ["sqlserver"],
        NodeKinds: [],
        IncludeConnectionRoot: true,
        ConnectionRootProviderIds: ["sqlserver"]);

    /// <summary>Empty: a document tool collects nothing. The tab is the interface.</summary>
    public IReadOnlyList<ToolField> Fields { get; } = [];

    /// <summary>This tool is SQL Server's Activity Monitor, so the host's own "Activity Monitor…" item on a
    /// connection root opens it and it stays out of the Tools submenu (SE-251). It replaces a feature that
    /// was already there; only its implementation moved into this plugin, not its place in the menu.</summary>
    public bool IsActivityMonitor => true;

    /// <summary>Never called — an <see cref="IToolDocumentUi"/> tool acts by opening its tab.</summary>
    public Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// The tab-strip glyph: a pulse trace, in the stroked 24×24 idiom the host's own tab icons use
    /// (Lucide-derived, stroke-width 2, round joins). Written out rather than borrowed — a plugin cannot
    /// reach the host's icon resources across the load-context boundary.
    /// </summary>
    Geometry? IToolDocumentUi.Icon { get; } = Geometry.Parse("M3 12 h4 l3 -8 l4 16 l3 -8 h4");

    public Control CreateDocument(IToolDocumentContext context) => new ActivityMonitorView(context);
}
