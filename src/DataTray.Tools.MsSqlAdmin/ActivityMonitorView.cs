using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// The Activity Monitor tab: SSMS's Overview strip over its five grids, refreshing itself on a timer.
/// </summary>
/// <remarks>
/// <para>Everything on screen is read from DMVs; the one thing the tab can change on the server is killing
/// a session, which is behind a confirmation because it is the one action here that loses somebody's
/// work.</para>
/// <para>Implements <see cref="IDisposable"/> so the host stops the timer and the in-flight refresh when
/// the tab closes — without it the connection would keep being polled every ten seconds for the life of
/// the app.</para>
/// </remarks>
internal sealed class ActivityMonitorView : UserControl, IDisposable
{
    /// <summary>How far back the Resource Waits grid's "recent" column averages. SSMS shows the last
    /// interval beside a longer-run figure so a spike can be told from a trend.</summary>
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(1);

    private readonly IToolDocumentContext _context;
    private readonly IPluginLocalizer _loc;
    private readonly ActivitySampler _sampler;
    private readonly CancellationTokenSource _cancellation = new();

    private readonly ActivityChart _cpu;
    private readonly ActivityChart _waitingTasks;
    private readonly ActivityChart _databaseIo;
    private readonly ActivityChart _batchRequests;

    private readonly ActivityGrid _processes;
    private readonly ActivityGrid _waits;
    private readonly ActivityGrid _files;
    private readonly ActivityGrid _recentQueries;
    private readonly ActivityGrid _activeQueries;

    private readonly ComboBox _interval = new() { MinWidth = 90, FontSize = 12 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75, FontSize = 12 };

    // Every sample since the tab opened, trimmed to what the graphs and the "recent" column still need.
    private readonly List<ActivitySample> _history = [];

    private DispatcherTimer? _timer;
    private bool _refreshing;

    public ActivityMonitorView(IToolDocumentContext context)
    {
        _context = context;
        _loc = context.Localizer;
        _sampler = new ActivitySampler(context.Provider, context.Profile);

        _cpu = new ActivityChart(_loc.Get("activity.graph.cpu"), Color.FromRgb(90, 140, 240), fixedMax: 100);
        _waitingTasks = new ActivityChart(_loc.Get("activity.graph.waitingTasks"), Color.FromRgb(60, 170, 160));
        _databaseIo = new ActivityChart(_loc.Get("activity.graph.io"), Color.FromRgb(220, 160, 60));
        _batchRequests = new ActivityChart(_loc.Get("activity.graph.batchRequests"), Color.FromRgb(150, 120, 230));

        var filterAll = _loc.Get("activity.filterAll");
        _processes = new ActivityGrid(
            _loc.Get("activity.section.processes"),
            ActivityTables.ProcessHeaders,
            // Only Wait Resource is pinned; it can hold a page identifier that would stretch the row.
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 220, 0, 0, 0, 0, 0],
            filterAll,
            height: 260,
            // SSMS opens on user processes only; the engine's own sixty background tasks are a different
            // question from the one anyone opens this grid to answer.
            filterColumn: 1,
            filterText: "1");

        _waits = new ActivityGrid(
            _loc.Get("activity.section.waits"),
            ActivityTables.WaitHeaders,
            [0, 0, 0, 0, 0],
            filterAll,
            height: 200);

        _files = new ActivityGrid(
            _loc.Get("activity.section.fileIo"),
            ActivityTables.FileIoHeaders,
            [0, 420, 0, 0, 0],
            filterAll,
            height: 200);

        _recentQueries = new ActivityGrid(
            _loc.Get("activity.section.recentQueries"),
            ActivityTables.RecentQueryHeaders,
            [420, 0, 0, 0, 0, 0, 0, 0, 0],
            filterAll,
            height: 220);

        _activeQueries = new ActivityGrid(
            _loc.Get("activity.section.activeQueries"),
            ActivityTables.ActiveQueryHeaders,
            [420, 0, 0, 0, 0, 0, 0, 0, 0],
            filterAll,
            height: 200);

        _processes.SetRowMenu(BuildProcessMenu());

        Content = BuildLayout();
        StartTimer(10);
    }

    private Control BuildLayout()
    {
        _interval.ItemsSource = new[]
        {
            new IntervalOption(_loc.Get("activity.refresh.off"), 0),
            new IntervalOption("5s", 5),
            new IntervalOption("10s", 10),
            new IntervalOption("30s", 30),
            new IntervalOption("60s", 60)
        };
        // SSMS's own default. Two of these grids show rates, and a rate needs two samples before it can say
        // anything at all, so the tab starts polling rather than waiting to be asked.
        _interval.SelectedIndex = 2;
        _interval.SelectionChanged += (_, _) =>
        {
            if (_interval.SelectedItem is IntervalOption option)
            {
                StartTimer(option.Seconds);
            }
        };

        var refreshNow = new Button { Content = _loc.Get("activity.refreshNow"), FontSize = 12 };
        refreshNow.Click += (_, _) => _ = RefreshAsync();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 8),
            Children =
            {
                new TextBlock
                {
                    Text = _loc.Get("activity.refresh"),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12
                },
                _interval,
                refreshNow,
                _status
            }
        };

        var graphs = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            Margin = new Thickness(4)
        };

        var charts = new[] { _cpu, _waitingTasks, _databaseIo, _batchRequests };
        for (var i = 0; i < charts.Length; i++)
        {
            charts[i].Margin = new Thickness(6, 2);
            Grid.SetColumn(charts[i], i);
            graphs.Children.Add(charts[i]);
        }

        var overview = new Expander
        {
            Header = _loc.Get("activity.section.overview"),
            IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8),
            Content = graphs
        };

        var body = new StackPanel
        {
            Margin = new Thickness(10, 0, 10, 10),
            Children =
            {
                overview,
                _processes.Section,
                _waits.Section,
                _files.Section,
                _recentQueries.Section,
                _activeQueries.Section
            }
        };

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        return root;
    }

    private void StartTimer(int seconds)
    {
        _timer?.Stop();
        _timer = null;

        if (seconds > 0)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _timer.Tick += (_, _) => _ = RefreshAsync();
            _timer.Start();
        }

        // Load straight away on open and on every interval change, so the tab never sits empty waiting for
        // the first tick.
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        // A sample that outlives its interval must not stack up behind the timer: on a busy server the DMV
        // script can take longer than five seconds, and queueing them would make the monitor the load.
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var seconds = _interval.SelectedItem is IntervalOption { Seconds: > 0 } option ? option.Seconds : 10;
            var sample = await _sampler.ReadAsync(seconds, _cancellation.Token);
            Apply(sample);
            _status.Text = _loc.Get("activity.updated", DateTime.Now.ToString("T", CultureInfo.CurrentCulture));
        }
        catch (OperationCanceledException)
        {
            // The tab was closed mid-refresh.
        }
        catch (Exception ex)
        {
            // One failed refresh is not worth clearing the screen for — the last sample stays up, with the
            // reason beside the clock, and the next tick may well succeed (a failover, a blocked DMV).
            _status.Text = _loc.Get("activity.error", ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Apply(ActivitySample sample)
    {
        var previous = _history.Count > 0 ? _history[^1] : null;
        var baseline = _history.FirstOrDefault(s => sample.TakenAt - s.TakenAt <= RecentWindow) ?? previous;

        _history.Add(sample);
        if (_history.Count > ActivityChart.Capacity)
        {
            _history.RemoveRange(0, _history.Count - ActivityChart.Capacity);
        }

        _processes.Update(ActivityTables.Processes(sample));
        _waits.Update(ActivityTables.ResourceWaits(sample, previous, baseline));
        _files.Update(ActivityTables.DataFileIo(sample, previous));
        _recentQueries.Update(ActivityTables.RecentQueries(sample, previous));
        _activeQueries.Update(ActivityTables.ActiveQueries(sample));

        UpdateGraphs(sample, previous);
    }

    private void UpdateGraphs(ActivitySample sample, ActivitySample? previous)
    {
        var seconds = previous is null ? 0 : (sample.TakenAt - previous.TakenAt).TotalSeconds;

        var processorTime = ActivityRates.ProcessorTime(sample.Counters, previous?.Counters);
        _cpu.Add(
            processorTime,
            processorTime is { } cpu
                ? ActivityRates.Number(cpu) + "%"
                : _loc.Get("activity.graph.unavailable"));

        _waitingTasks.Add(
            sample.Counters.WaitingTasks,
            sample.Counters.WaitingTasks.ToString(CultureInfo.CurrentCulture));

        var readWritten = sample.Files.Sum(f => f.BytesRead + f.BytesWritten);
        var wasReadWritten = previous?.Files.Sum(f => f.BytesRead + f.BytesWritten) ?? 0;
        var mbPerSecond = ActivityRates.PerSecond(readWritten, wasReadWritten, seconds) / (1024 * 1024);
        _databaseIo.Add(mbPerSecond, _loc.Get("activity.graph.mbPerSec", ActivityRates.Number(mbPerSecond)));

        var batches = ActivityRates.PerSecond(
            sample.Counters.BatchRequests,
            previous?.Counters.BatchRequests ?? 0,
            seconds);
        _batchRequests.Add(batches, ActivityRates.Number(batches));
    }

    // SSMS puts Kill Process on the Processes grid's context menu. It is the only write this tab can make,
    // so it asks first — and it asks with the session's login and host in the question, because "kill 71"
    // is not something anyone can sanity-check.
    private ContextMenu BuildProcessMenu()
    {
        var kill = new MenuItem { Header = _loc.Get("activity.kill") };
        kill.Click += async (_, _) =>
        {
            if (_processes.SelectedCells is not { } cells || cells.Length < 15)
            {
                return;
            }

            if (!int.TryParse(cells[0], NumberStyles.Any, CultureInfo.CurrentCulture, out var sessionId))
            {
                return;
            }

            await KillAsync(sessionId, cells[2], cells[13]);
        };

        return new ContextMenu { ItemsSource = new[] { kill } };
    }

    private async Task KillAsync(int sessionId, string login, string host)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var confirmed = await ConfirmKillWindow.ShowAsync(owner, _loc, sessionId, login, host);
        if (!confirmed)
        {
            return;
        }

        try
        {
            // sessionId came through int.TryParse, so it can only ever be an integer in the KILL text.
            await _context.Provider.ExecuteDdlAsync(_context.Profile, $"KILL {sessionId}", _cancellation.Token);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _status.Text = _loc.Get("activity.error", ex.Message);
        }
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    private sealed record IntervalOption(string Label, int Seconds)
    {
        public override string ToString() => Label;
    }
}
