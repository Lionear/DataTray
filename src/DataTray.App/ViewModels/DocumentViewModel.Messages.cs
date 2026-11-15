using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataTray.Sdk.Query;

namespace DataTray.App.ViewModels;

// SE-300: server messages and run progress. The provider reports into Messages from the driver's thread; the
// view calls Tick on a timer while a run is going, which drains the queue, moves the clock and decides whether
// the Messages tab has earned its place.
public partial class DocumentViewModel
{
    // A run shorter than this that only produced bare row counts keeps today's look: no Messages tab.
    private static readonly TimeSpan QuietRunLimit = TimeSpan.FromSeconds(1);

    private DateTimeOffset _runStartedAt;
    private TimeSpan _runElapsed;
    private bool _runFailed;
    private bool _tabPickedByUser;
    private bool _messagesSupported;
    private int _runNumber;
    private int _errorsBeforeRun;

    private IDisposable? _ticker;

    /// <summary>Starts a repeating UI-thread callback that runs while it returns true; set by the host. Without
    /// one (tests) nothing ticks on its own and the run's end does the last drain.</summary>
    public Func<Func<bool>, IDisposable>? Ticker { get; set; }

    /// <summary>This tab's Messages log.</summary>
    public QueryMessageLog Messages { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunStatusText))]
    [NotifyPropertyChangedFor(nameof(HasRunStatus))]
    [NotifyPropertyChangedFor(nameof(ShowRunClock))]
    [NotifyPropertyChangedFor(nameof(IsRunActive))]
    [NotifyPropertyChangedFor(nameof(IsRunCompleted))]
    [NotifyPropertyChangedFor(nameof(IsRunCancelled))]
    [NotifyPropertyChangedFor(nameof(IsRunFailed))]
    [NotifyPropertyChangedFor(nameof(ShowRunningLastMessage))]
    [NotifyPropertyChangedFor(nameof(HasMessagesPill))]
    private QueryRunOutcome _runOutcome;

    public bool IsRunActive => RunOutcome == QueryRunOutcome.Running;

    public bool IsRunCompleted => RunOutcome == QueryRunOutcome.Completed;

    public bool IsRunCancelled => RunOutcome == QueryRunOutcome.Cancelled;

    public bool IsRunFailed => RunOutcome == QueryRunOutcome.Failed;

    /// <summary>The latest message beside "Running · 00:42" in the status bar.</summary>
    public bool ShowRunningLastMessage => IsRunActive && Messages.LastText is not null;

    /// <summary>The status-bar pill that opens the Messages tab: only for an engine with messages, once there are any.</summary>
    public bool HasMessagesPill => HasRunStatus && _messagesSupported && Messages.Total > 0;

    /// <summary>The count on the tab and the pill: the real total, shortened past a thousand (<c>48.2k</c>).</summary>
    public string MessagesBadge => Messages.Total < 1000
        ? Messages.Total.ToString(CultureInfo.InvariantCulture)
        : (Messages.Total / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";

    public string MessagesTruncatedText => Loc.Get("MessagesTruncated", Messages.Capacity, Messages.Total);

    public string MessagesNewText => Loc.Get("MessagesNew", Messages.NewWhilePaused);

    public string MessagesFilterCountText => Loc.Get("MessagesFilterCount", Messages.Visible.Count, Messages.Total);

    // The log changes on its own (flushes, filters); re-announce what the view derives from it.
    private void WireMessages()
    {
        Messages.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(QueryMessageLog.Total):
                    OnPropertyChanged(nameof(MessagesBadge));
                    OnPropertyChanged(nameof(MessagesTruncatedText));
                    OnPropertyChanged(nameof(MessagesFilterCountText));
                    OnPropertyChanged(nameof(HasMessagesPill));
                    OnPropertyChanged(nameof(RunStatusText));
                    break;
                case nameof(QueryMessageLog.NewWhilePaused):
                    OnPropertyChanged(nameof(MessagesNewText));
                    break;
                case nameof(QueryMessageLog.LastText):
                    OnPropertyChanged(nameof(ShowRunningLastMessage));
                    break;
                case nameof(QueryMessageLog.FirstErrorCode):
                    OnPropertyChanged(nameof(RunStatusText));
                    break;
            }
        };
        Messages.Visible.CollectionChanged += (_, _) => OnPropertyChanged(nameof(MessagesFilterCountText));
    }

    /// <summary>Elapsed time of the current (or last) run, <c>00:42</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunStatusText))]
    private string _runClock = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResultTabs))]
    [NotifyPropertyChangedFor(nameof(ShowResultStrip))]
    private bool _showMessagesTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridSurface))]
    [NotifyPropertyChangedFor(nameof(IsViewerSurface))]
    [NotifyPropertyChangedFor(nameof(ShowEditToolbar))]
    private bool _isMessagesSelected;

    /// <summary>Whether the provider of the last run reports messages at all; without it there is no tab and
    /// no status-bar pill, only the clock.</summary>
    public bool SupportsMessages => _messagesSupported;

    /// <summary>The result-set tabs show beside Messages even for one result set, so there is something to switch to.</summary>
    public bool ShowResultTabs => HasMultipleResultSets || (ShowMessagesTab && HasGridResult);

    /// <summary>Row 4 of the document: result tabs, Messages, or the view switcher.</summary>
    public bool ShowResultStrip => ShowResultTabs || ShowMessagesTab || HasViewSwitcher;

    private bool HasGridResult => ResultSets.Any(t => t.Set.Columns.Count > 0);

    /// <summary>The clock beside Run/Stop: while running, and frozen after a stop or a failure.</summary>
    public bool ShowRunClock => RunOutcome is QueryRunOutcome.Running or QueryRunOutcome.Cancelled or QueryRunOutcome.Failed;

    public bool HasRunStatus => IsQueryMode && RunOutcome != QueryRunOutcome.None;

    /// <summary>The status-bar line for this tab's run: <c>Running · 00:42</c>, <c>Completed · 58.4 s</c>,
    /// <c>Cancelled at 00:42 · 12 messages kept</c>, <c>Failed at 00:14 · Msg 547</c>.</summary>
    public string RunStatusText => RunOutcome switch
    {
        QueryRunOutcome.Running => Loc.Get("RunStatusRunning", RunClock),
        QueryRunOutcome.Completed => Loc.Get("RunStatusCompleted", _runElapsed.TotalSeconds.ToString("0.0", Loc.Culture)),
        QueryRunOutcome.Cancelled when _messagesSupported => Loc.Get("RunStatusCancelledKept", RunClock, Messages.Total),
        QueryRunOutcome.Cancelled => Loc.Get("RunStatusCancelled", RunClock),
        QueryRunOutcome.Failed when Messages.FirstErrorCode is { } code => Loc.Get("RunStatusFailedCode", RunClock, code),
        QueryRunOutcome.Failed => Loc.Get("RunStatusFailed", RunClock),
        _ => string.Empty,
    };

    /// <summary>Drain the message queue, move the clock and re-decide the Messages tab. Called by the view's
    /// timer while a run is going, and once more when it ends.</summary>
    public void Tick()
    {
        Messages.Flush();
        if (RunOutcome == QueryRunOutcome.Running)
        {
            _runElapsed = Messages.Clock.GetUtcNow() - _runStartedAt;
            RunClock = FormatClock(_runElapsed);
            OnPropertyChanged(nameof(RunStatusText));
        }

        UpdateMessagesTab(finished: RunOutcome != QueryRunOutcome.Running);
    }

    [RelayCommand]
    private void SelectMessages()
    {
        _tabPickedByUser = true;
        IsMessagesSelected = true;
    }

    partial void OnIsMessagesSelectedChanged(bool value) => MarkSelected(SelectedResultSetIndex);

    // A fresh Run / Run at cursor / Explain: the previous run's messages go, unless the user keeps them.
    private void StartMessagesForRun()
    {
        _messagesSupported = _providers.TryGet(Connection.ProviderId, out var provider) && provider.SupportsQueryMessages;
        OnPropertyChanged(nameof(SupportsMessages));
        OnPropertyChanged(nameof(HasMessagesPill));
        _runNumber++;
        if (_settingsStore.Load().KeepQueryMessagesBetweenRuns && Messages.Total > 0)
        {
            Messages.AddSeparator(Loc.Get("MessagesRunSeparator", _runNumber,
                Messages.Clock.GetLocalNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        }
        else
        {
            Messages.Clear();
            ShowMessagesTab = false;
            IsMessagesSelected = false;
        }

        _errorsBeforeRun = Messages.ErrorCount;
        _tabPickedByUser = false;
    }

    private void BeginRunProgress()
    {
        _runFailed = false;
        _runStartedAt = Messages.Clock.GetUtcNow();
        _runElapsed = TimeSpan.Zero;
        RunClock = FormatClock(TimeSpan.Zero);
        RunOutcome = QueryRunOutcome.Running;
        _ticker = Ticker?.Invoke(() =>
        {
            Tick();
            return RunOutcome == QueryRunOutcome.Running;
        });
    }

    private void EndRunProgress(bool cancelled)
    {
        _ticker?.Dispose();
        _ticker = null;
        Messages.Flush();
        _runElapsed = Messages.Clock.GetUtcNow() - _runStartedAt;
        RunClock = FormatClock(_runElapsed);
        if (cancelled)
        {
            Messages.AddClientLine(Loc.Get("QueryCancelledAfter", RunClock));
            Messages.Flush();
        }

        RunOutcome = cancelled ? QueryRunOutcome.Cancelled
            : _runFailed ? QueryRunOutcome.Failed
            : QueryRunOutcome.Completed;
        UpdateMessagesTab(finished: true);
    }

    // A failure during a run lands in the timeline too — unless the provider already reported it, with the
    // server's own header, in which case a second, bare copy would only be noise.
    private void NoteRunFailure(string message)
    {
        if (!IsRunning)
        {
            return;
        }

        _runFailed = true;
        Messages.Flush();
        if (Messages.ErrorCount <= _errorsBeforeRun)
        {
            Messages.Report(new QueryMessage(QueryMessageKind.Error, message));
        }
    }

    private void UpdateMessagesTab(bool finished)
    {
        if (!_messagesSupported)
        {
            ShowMessagesTab = false;
            IsMessagesSelected = false;
            return;
        }

        var show = ShowMessagesTab
            || _runElapsed > QuietRunLimit
            || Messages.LastText is not null
            || RunOutcome is QueryRunOutcome.Cancelled or QueryRunOutcome.Failed
            || (finished && HasMultipleResultSets);
        ShowMessagesTab = show;
        OnPropertyChanged(nameof(ShowResultTabs));
        OnPropertyChanged(nameof(ShowResultStrip));

        if (!show || _tabPickedByUser)
        {
            return;
        }

        // While running, whatever grid is showing is the previous run's. Once done, a fresh grid wins — but
        // after a stop or a failure the grid was never replaced, so the messages are what tells the story.
        IsMessagesSelected = !finished || RunOutcome != QueryRunOutcome.Completed || !HasGridResult;
    }

    private static string FormatClock(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
}
