using System.Collections.Concurrent;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DataTray.Sdk.Query;

namespace DataTray.App.ViewModels;

/// <summary>
/// A query tab's Messages log (SE-300). Providers report from the driver's own threads; <see cref="Report"/>
/// only stamps and queues, and the UI drains the queue with <see cref="Flush"/> on a timer, so a script that
/// prints thousands of lines costs one list update per tick rather than one per line. Only the latest
/// <c>capacity</c> lines are kept; the counters keep counting what was dropped.
/// </summary>
public sealed partial class QueryMessageLog(TimeProvider clock, int capacity = QueryMessageLog.DefaultCapacity)
    : ObservableObject, IProgress<QueryMessage>
{
    public const int DefaultCapacity = 10_000;

    private readonly ConcurrentQueue<QueryMessageEntry> _incoming = new();
    // ponytail: List.RemoveRange shifts the kept lines once per trimming flush; a real ring buffer if that
    // ever shows up in a profile.
    private readonly List<QueryMessageEntry> _kept = [];

    public TimeProvider Clock { get; } = clock;

    /// <summary>The kept lines that pass the filter, in arrival order — what the list shows.</summary>
    public BulkObservableCollection<QueryMessageEntry> Visible { get; } = [];

    /// <summary>Every message received since the last clear, dropped ones included (separators excluded).</summary>
    [ObservableProperty]
    private int _total;

    /// <summary>Info lines, row counts and client lines.</summary>
    [ObservableProperty]
    private int _infoCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _errorCount;

    /// <summary>Lines no longer kept because the log reached its capacity.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTruncated))]
    private int _dropped;

    /// <summary>The code of the first error (<c>Msg 547</c>), for the status bar.</summary>
    [ObservableProperty]
    private string? _firstErrorCode;

    /// <summary>The latest line that is not a bare row count, for the status bar while a run is going.</summary>
    [ObservableProperty]
    private string? _lastText;

    public bool IsTruncated => Dropped > 0;

    public int Capacity => capacity;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _showInfo = true;

    [ObservableProperty]
    private bool _showWarnings = true;

    [ObservableProperty]
    private bool _showErrors = true;

    partial void OnFilterTextChanged(string value) => Refilter();
    partial void OnShowInfoChanged(bool value) => Refilter();
    partial void OnShowWarningsChanged(bool value) => Refilter();
    partial void OnShowErrorsChanged(bool value) => Refilter();

    public bool IsFiltered => FilterText.Length > 0 || !ShowInfo || !ShowWarnings || !ShowErrors;

    /// <summary>Queue a server message. Safe from any thread; nothing shows until the next <see cref="Flush"/>.</summary>
    public void Report(QueryMessage value) =>
        _incoming.Enqueue(new QueryMessageEntry(value.Kind, value.Text, clock.GetLocalNow())
        {
            Header = value.Header,
            Code = value.Code,
        });

    /// <summary>Queue a line the app writes itself (the cancel note). It counts as an info message.</summary>
    public void AddClientLine(string text) =>
        _incoming.Enqueue(new QueryMessageEntry(QueryMessageKind.Info, text, clock.GetLocalNow()) { IsClient = true });

    /// <summary>Queue the divider between two runs whose messages are both kept.</summary>
    public void AddSeparator(string text) =>
        _incoming.Enqueue(new QueryMessageEntry(QueryMessageKind.Info, text, clock.GetLocalNow()) { IsSeparator = true });

    /// <summary>Move everything queued into the log. UI thread only. Returns true when anything arrived.</summary>
    public bool Flush()
    {
        var arrived = new List<QueryMessageEntry>();
        while (_incoming.TryDequeue(out var entry))
        {
            arrived.Add(entry);
        }

        if (arrived.Count == 0)
        {
            return false;
        }

        foreach (var entry in arrived)
        {
            Count(entry);
        }

        _kept.AddRange(arrived);
        var overflow = _kept.Count - capacity;
        if (overflow > 0)
        {
            _kept.RemoveRange(0, overflow);
            Dropped += overflow;
            Visible.ReplaceAll(_kept.Where(Matches));
        }
        else
        {
            foreach (var entry in arrived.Where(Matches))
            {
                Visible.Add(entry);
            }
        }

        return true;
    }

    /// <summary>Forget everything, queued lines included.</summary>
    public void Clear()
    {
        _incoming.Clear();
        _kept.Clear();
        Visible.Clear();
        Total = InfoCount = WarningCount = ErrorCount = Dropped = 0;
        FirstErrorCode = null;
        LastText = null;
    }

    /// <summary><paramref name="entries"/> as <c>[time] text</c>, one line each; an error's server header goes
    /// on its own line in front of the text.</summary>
    public string CopyText(IEnumerable<QueryMessageEntry> entries)
    {
        var text = new StringBuilder();
        foreach (var entry in entries)
        {
            if (entry.Header is { } header)
            {
                text.Append('[').Append(entry.TimeText).Append("] ").AppendLine(header);
            }

            text.Append('[').Append(entry.TimeText).Append("] ").AppendLine(entry.Text);
        }

        return text.ToString().TrimEnd('\r', '\n');
    }

    private void Count(QueryMessageEntry entry)
    {
        if (entry.IsSeparator)
        {
            return;
        }

        Total++;
        switch (entry.Kind)
        {
            case QueryMessageKind.Warning:
                WarningCount++;
                break;
            case QueryMessageKind.Error:
                ErrorCount++;
                FirstErrorCode ??= entry.Code;
                break;
            default:
                InfoCount++;
                break;
        }

        if (entry.Kind != QueryMessageKind.RowsAffected)
        {
            LastText = entry.Text;
        }
    }

    private bool Matches(QueryMessageEntry entry)
    {
        if (entry.IsSeparator)
        {
            return FilterText.Length == 0;
        }

        var levelShown = entry.Kind switch
        {
            QueryMessageKind.Warning => ShowWarnings,
            QueryMessageKind.Error => ShowErrors,
            _ => ShowInfo,
        };

        return levelShown
            && (FilterText.Length == 0
                || entry.Text.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                || entry.Header?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void Refilter()
    {
        OnPropertyChanged(nameof(IsFiltered));
        Visible.ReplaceAll(_kept.Where(Matches));
    }
}
