using DataTray.Sdk.Query;

namespace DataTray.Sdk.Viewers;

/// <summary>
/// A read-only snapshot of the result set a viewer renders. The host owns the live, editable model and
/// does not hand it out: a viewer is a rendering, never a second writer. Rows are index-aligned with
/// <see cref="Columns"/>, exactly as in <see cref="QueryResult"/>, and <see cref="ResultColumn.ClrType"/>
/// is the only type information a viewer gets.
/// </summary>
/// <param name="Columns">The result set's columns, in grid order.</param>
/// <param name="Rows">The rows currently held by the grid — one page, not the whole table.</param>
/// <param name="ProviderId">Which engine produced this, for viewers that want to specialise.</param>
public sealed record ResultView(
    IReadOnlyList<ResultColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    string ProviderId)
{
    /// <summary>How long the query behind this result set took, when the host knows.</summary>
    public TimeSpan? Elapsed { get; init; }

    /// <summary>Qualified table the result traces back to, when it traces back to exactly one — the same
    /// condition that makes the grid editable. Null for a join, an aggregate, a view or a file.</summary>
    public string? QualifiedTable { get; init; }
}
