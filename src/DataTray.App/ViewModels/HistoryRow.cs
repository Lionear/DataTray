using CommunityToolkit.Mvvm.ComponentModel;
using DataTray.Core.History;

namespace DataTray.App.ViewModels;

/// <summary>
/// One row in the history panel. Wraps a <see cref="QueryHistoryEntry"/> so the star can carry state the
/// stored entry doesn't have (SE-31) — and so the same list can also show starred queries that history
/// itself no longer holds, after a clear or once they fall out of the ring buffer.
/// </summary>
public sealed partial class HistoryRow : ViewModelBase
{
    private HistoryRow(string sql, string? connectionName, QueryHistoryEntry? entry, bool isFavorite)
    {
        Sql = sql;
        ConnectionName = connectionName ?? string.Empty;
        Entry = entry;
        _isFavorite = isFavorite;
    }

    public static HistoryRow ForEntry(QueryHistoryEntry entry, bool isFavorite) =>
        new(entry.Sql, entry.ConnectionName, entry, isFavorite);

    /// <summary>A starred query with no matching history entry left — it still runs, it just has no
    /// row count or duration to show.</summary>
    public static HistoryRow ForFavorite(FavoriteQuery favorite) =>
        new(favorite.Sql, favorite.ConnectionName, entry: null, isFavorite: true);

    /// <summary>The history entry behind this row, or null for a favorite that outlived it.</summary>
    public QueryHistoryEntry? Entry { get; }

    public string Sql { get; }

    public string ConnectionName { get; }

    public int RowCount => Entry?.RowCount ?? 0;

    public long DurationMs => Entry?.DurationMs ?? 0;

    public QueryHistorySource Source => Entry?.Source ?? QueryHistorySource.User;

    /// <summary>Whether this row's stats line has anything to say.</summary>
    public bool HasStats => Entry is not null;

    [ObservableProperty]
    private bool _isFavorite;
}
