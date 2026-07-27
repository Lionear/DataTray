using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataTray.Core.History;
using DataTray.Core.Localization;
using DataTray.Core.Logging;
using DataTray.Core.Settings;

namespace DataTray.App.ViewModels;

/// <summary>
/// Backs the Query Log window: reads the opt-in audit log through <see cref="IQueryLog"/>, applies the
/// toolbar filters, and refreshes live when a new query is logged (including AI/MCP runs that arrive while
/// the window is open). Clipboard/file-export are handled by the window code-behind (it owns the TopLevel);
/// this only exposes the data and the Clear action.
/// </summary>
public partial class QueryLogViewModel : ObservableObject, IDisposable
{
    private readonly IQueryLog _log;

    public ILocalizer Loc { get; }

    public ObservableCollection<QueryLogRow> Entries { get; } = [];

    [ObservableProperty]
    private QueryLogRow? _selectedEntry;

    [ObservableProperty]
    private string _search = string.Empty;

    /// <summary>0 = all, 1 = application, 2 = AI/MCP.</summary>
    [ObservableProperty]
    private int _sourceFilter;

    /// <summary>0 = all, 1 = success, 2 = error.</summary>
    [ObservableProperty]
    private int _statusFilter;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Set by the view: open the given logged query in a new editor tab (reuses the main window's
    /// history-open path, which resolves the connection and adds a query tab).</summary>
    public Action<QueryHistoryEntry>? OpenInEditorRequested { get; set; }

    public QueryLogViewModel(IQueryLog log, ILocalizer localizer, IAppSettingsStore settingsStore)
    {
        _log = log;
        Loc = localizer;
        _log.Changed += OnLogChanged;
        LogPolicyNotice = BuildPolicyNotice(settingsStore.Load(), localizer);
        Reload();
    }

    /// <summary>A banner shown when logging is (partly) off, so an empty list reads as "nothing is being
    /// recorded" rather than "no queries yet" (SE-182). Null when everything is being logged. Reflects the
    /// policy at the moment the window opens — the window is recreated per open, so it's always current.</summary>
    public string? LogPolicyNotice { get; }

    public bool HasLogPolicyNotice => LogPolicyNotice is not null;

    private static string? BuildPolicyNotice(AppSettings s, ILocalizer loc) =>
        !s.QueryLogEnabled || (!s.QueryLogApp && !s.QueryLogMcp) ? loc["QueryLogPolicyOff"]
        : !s.QueryLogApp ? loc["QueryLogPolicyMcpOnly"]
        : !s.QueryLogMcp ? loc["QueryLogPolicyAppOnly"]
        : null;

    private void OnLogChanged() => Dispatcher.UIThread.Post(Reload);

    partial void OnSearchChanged(string value) => Reload();
    partial void OnSourceFilterChanged(int value) => Reload();
    partial void OnStatusFilterChanged(int value) => Reload();

    private void Reload()
    {
        var filter = new QueryLogFilter
        {
            Text = string.IsNullOrWhiteSpace(Search) ? null : Search,
            Source = SourceFilter switch
            {
                1 => QueryHistorySource.User,
                2 => QueryHistorySource.Ai,
                _ => null
            },
            Success = StatusFilter switch
            {
                1 => true,
                2 => false,
                _ => null
            },
            Limit = 5000
        };

        var selectedId = SelectedEntry?.Entry.Id;

        Entries.Clear();
        foreach (var entry in _log.Read(filter))
        {
            Entries.Add(new QueryLogRow { Entry = entry });
        }

        // Keep the selection on the same entry across a refresh; otherwise select the newest.
        SelectedEntry = (selectedId is not null ? Entries.FirstOrDefault(r => r.Entry.Id == selectedId) : null)
                        ?? Entries.FirstOrDefault();

        Summary = Loc.Get("QueryLogSummary", Entries.Count);
    }

    [RelayCommand]
    private void Clear() => _log.Clear(); // Changed → Reload repaints the empty list.

    [RelayCommand]
    private void OpenInEditor()
    {
        if (SelectedEntry is { } row)
        {
            OpenInEditorRequested?.Invoke(row.Entry);
        }
    }

    // Detach from the store's Changed event when the window closes so the (transient) VM can be collected.
    public void Dispose() => _log.Changed -= OnLogChanged;
}
