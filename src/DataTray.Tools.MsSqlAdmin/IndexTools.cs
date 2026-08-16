using Avalonia.Controls;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// The index maintenance actions SSMS offers on a table's <b>Indexes</b> node and on a single index
/// (SE-249). One class per action would be seven near-identical files; they differ only in which verb they
/// run and whether they act on one index or on all of them, so that difference is the two abstract members
/// below and everything else is shared.
/// </summary>
/// <remarks>
/// The target table comes from <see cref="ToolExecutionContext.NodePath"/> (tool API v7). It cannot come
/// from the node itself: an "Indexes" folder is called "Indexes" under every table in the database, and an
/// index name is only unique within its table, so a tool that looked the name up in <c>sys.indexes</c>
/// would maintain whichever table matched first.
/// </remarks>
public abstract class IndexToolBase : IToolPlugin, ICustomToolUi
{
    protected abstract IndexAction Action { get; }

    /// <summary>True for the folder actions ("Rebuild All"), which act on every index of the table.</summary>
    protected abstract bool AllIndexes { get; }

    public abstract string Id { get; }

    public abstract string Title { get; }

    public abstract string? TitleKey { get; }

    public string DialogTitle => Title;

    public string? DialogTitleKey => TitleKey;

    public string? Description => null;

    public abstract string? DescriptionKey { get; }

    public ToolTarget Target => new(
        ProviderIds: ["sqlserver"],
        NodeKinds: [AllIndexes ? DbNodeKind.IndexFolder : DbNodeKind.Index]);

    /// <summary>The action is the whole input; there is nothing to collect. The dialog is therefore a
    /// confirmation rather than a form — see <see cref="CreateView"/> for what it puts in front of it.</summary>
    public IReadOnlyList<ToolField> Fields { get; } = [];

    /// <summary>Route B. With no fields and no view the host's generic dialog has nothing to render between
    /// the title and the buttons, so these seven actions asked "rebuild every index on this table?" over an
    /// empty body. What belongs there is what SSMS shows: the current fragmentation per index, so the
    /// decision is made on the numbers it turns on rather than on the table's name.</summary>
    public Control CreateView(IToolUiContext context) => new IndexFragmentationView(
        context,
        // The host resolves DescriptionKey itself for a Route-A dialog, but its description block lives
        // inside the fields area a Route-B view replaces — so the view carries it, resolved the same way.
        DescriptionKey is { } key && context.Localizer.Contains(key) ? context.Localizer[key] : Description,
        AllIndexes ? null : context.Node?.Name);

    /// <summary>These are the node's own verbs, not extras offered on it, so they render straight on the
    /// context menu the way SSMS has them rather than under Tools ▸ (SE-253). The implementation being a
    /// plugin is a fact about this codebase, and should not cost the user a click.</summary>
    public bool IsNodeAction => true;

    /// <summary>Disabling an index makes it unusable until it is rebuilt — and disabling a clustered index
    /// takes the table's data offline with it — while dropping one is gone for good. Both get the host's
    /// destructive confirmation; rebuilding and reorganising do not, since they only cost time.</summary>
    public bool IsDestructive => Action is IndexAction.Disable or IndexAction.Drop;

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var table = context.Ancestor(DbNodeKind.Table)
            ?? throw new InvalidOperationException(context.Localizer["index.error.noTable"]);
        var schema = context.Ancestor(DbNodeKind.Schema);
        var index = AllIndexes ? null : context.Node?.Name;

        if (!AllIndexes && string.IsNullOrEmpty(index))
        {
            throw new InvalidOperationException(context.Localizer["index.error.noIndex"]);
        }

        if (Action == IndexAction.Drop)
        {
            await RefuseIfConstraintAsync(context, schema, table, index!, ct);
        }

        var sql = IndexStatements.Build(context.Provider.Dialect, Action, schema, table, index);
        progress.Report(new ToolProgress(context.Localizer.Get("index.progress.running", sql)));
        await context.Provider.ExecuteDdlAsync(context.Profile, sql, ct);
        progress.Report(new ToolProgress(
            context.Localizer.Get("index.progress.complete", index ?? table), 1.0));
    }

    // A primary key's index cannot be dropped with DROP INDEX. SQL Server's own refusal names the index and
    // talks about "an explicit DROP INDEX", which reads like a restriction on this user rather than like
    // "that is a constraint" — so the check happens here and says what to drop instead.
    private static async Task RefuseIfConstraintAsync(
        ToolExecutionContext context,
        string? schema,
        string table,
        string index,
        CancellationToken ct)
    {
        var sql = IndexStatements.ConstraintCheck(context.Provider.Dialect, schema, table, index);
        var result = await context.Provider.ExecuteQueryAsync(context.Profile, sql, ct);
        if (result.Rows.Count == 0)
        {
            return;
        }

        var row = result.Rows[0];
        if (Convert.ToBoolean(row[0]) || Convert.ToBoolean(row[1]))
        {
            throw new InvalidOperationException(context.Localizer.Get("index.error.constraint", index));
        }
    }
}

public sealed class RebuildAllIndexesTool : IndexToolBase
{
    protected override IndexAction Action => IndexAction.Rebuild;
    protected override bool AllIndexes => true;

    public override string Id => "mssql-index-rebuild-all";
    public override string Title => "Rebuild All";
    public override string? TitleKey => "index.rebuildAll.title";
    public override string? DescriptionKey => "index.rebuildAll.description";
}

public sealed class ReorganizeAllIndexesTool : IndexToolBase
{
    protected override IndexAction Action => IndexAction.Reorganize;
    protected override bool AllIndexes => true;

    public override string Id => "mssql-index-reorganize-all";
    public override string Title => "Reorganize All";
    public override string? TitleKey => "index.reorganizeAll.title";
    public override string? DescriptionKey => "index.reorganizeAll.description";
}

public sealed class DisableAllIndexesTool : IndexToolBase
{
    protected override IndexAction Action => IndexAction.Disable;
    protected override bool AllIndexes => true;

    public override string Id => "mssql-index-disable-all";
    public override string Title => "Disable All";
    public override string? TitleKey => "index.disableAll.title";
    public override string? DescriptionKey => "index.disableAll.description";
}

public sealed class RebuildIndexTool : IndexToolBase
{
    protected override IndexAction Action => IndexAction.Rebuild;
    protected override bool AllIndexes => false;

    public override string Id => "mssql-index-rebuild";
    public override string Title => "Rebuild";
    public override string? TitleKey => "index.rebuild.title";
    public override string? DescriptionKey => "index.rebuild.description";
}

public sealed class ReorganizeIndexTool : IndexToolBase
{
    protected override IndexAction Action => IndexAction.Reorganize;
    protected override bool AllIndexes => false;

    public override string Id => "mssql-index-reorganize";
    public override string Title => "Reorganize";
    public override string? TitleKey => "index.reorganize.title";
    public override string? DescriptionKey => "index.reorganize.description";
}

public sealed class DisableIndexTool : IndexToolBase
{
    protected override IndexAction Action => IndexAction.Disable;
    protected override bool AllIndexes => false;

    public override string Id => "mssql-index-disable";
    public override string Title => "Disable";
    public override string? TitleKey => "index.disable.title";
    public override string? DescriptionKey => "index.disable.description";
}

public sealed class DropIndexTool : IndexToolBase
{
    protected override IndexAction Action => IndexAction.Drop;
    protected override bool AllIndexes => false;

    public override string Id => "mssql-index-drop";
    public override string Title => "Drop…";
    public override string? TitleKey => "index.drop.title";
    public override string? DescriptionKey => "index.drop.description";
}
