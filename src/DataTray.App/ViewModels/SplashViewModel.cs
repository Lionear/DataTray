using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataTray.App.Startup;
using DataTray.Core.Localization;

namespace DataTray.App.ViewModels;

/// <summary>
/// The startup splash (SE-297): which step runs, whether it is slow, how it failed, and the tray rows that
/// draw it. Pure state; the window ticks it once a second and the runner reports into it.
/// </summary>
public sealed partial class SplashViewModel : ViewModelBase
{
    private readonly ILocalizer _loc;
    private readonly DateTimeOffset _runStartedAt;
    private readonly Dictionary<int, string> _keysByIndex = [];
    private DateTimeOffset _stepStartedAt;
    private StartupStep? _step;
    private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsFailed), nameof(IsReady))]
    private SplashPhase _phase;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _slowSuffix = string.Empty;
    [ObservableProperty] private string? _subLine;
    [ObservableProperty] private string? _detailLine;
    [ObservableProperty] private string _stepOf = string.Empty;
    [ObservableProperty] private string _accessibleStatus = string.Empty;
    [ObservableProperty] private bool _isSlow;
    [ObservableProperty] private bool _showWaysOut;
    [ObservableProperty] private int _waitedSeconds;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _canContinueWithout;
    [ObservableProperty] private IReadOnlyList<TrayRow> _rows = [];

    public SplashViewModel(ILocalizer loc, TimeProvider clock)
    {
        _loc = loc;
        Clock = clock;
        _runStartedAt = clock.GetUtcNow();
    }

    public ILocalizer Loc => _loc;

    public TimeProvider Clock { get; }

    public bool IsRunning => Phase == SplashPhase.Running;

    public bool IsFailed => Phase == SplashPhase.Failed;

    public bool IsReady => Phase == SplashPhase.Ready;

    public Action? RetryRequested { get; set; }

    public Action? ContinueWithoutRequested { get; set; }

    public Action? OpenLogRequested { get; set; }

    public Action? QuitRequested { get; set; }

    public void Report(StartupStep step)
    {
        _step = step;
        _keysByIndex[step.Index] = step.Key;
        _detail = null;
        _stepStartedAt = Clock.GetUtcNow();
        Phase = SplashPhase.Running;
        ErrorText = null;
        CanContinueWithout = false;
        IsSlow = false;
        ShowWaysOut = false;
        WaitedSeconds = 0;
        Refresh();
    }

    public void ReportDetail(string detail)
    {
        _detail = detail;
        Refresh();
    }

    public void Tick()
    {
        if (Phase != SplashPhase.Running || _step is null)
        {
            return;
        }

        var waited = Clock.GetUtcNow() - _stepStartedAt;
        WaitedSeconds = (int)waited.TotalSeconds;
        IsSlow = waited >= StartupThresholds.SlowAfter;
        ShowWaysOut = waited >= StartupThresholds.WaysOutAfter && StartupSteps.HasDegradedMode(_step.Key);
        Refresh();
    }

    public void Fail(Exception error)
    {
        Phase = SplashPhase.Failed;
        ErrorText = $"{error.GetType().Name}: {error.Message}";
        CanContinueWithout = _step is not null && StartupSteps.HasDegradedMode(_step.Key);
        ShowWaysOut = false;
        Refresh();
    }

    public void Complete()
    {
        Phase = SplashPhase.Ready;
        ShowWaysOut = false;
        Refresh();
    }

    [RelayCommand]
    private void Retry() => RetryRequested?.Invoke();

    [RelayCommand]
    private void ContinueWithout() => ContinueWithoutRequested?.Invoke();

    [RelayCommand]
    private void OpenLog() => OpenLogRequested?.Invoke();

    [RelayCommand]
    private void Quit() => QuitRequested?.Invoke();

    private void Refresh()
    {
        if (_step is null)
        {
            return;
        }

        var key = _step.Key;
        var stepTitle = _loc[StepKey(key)];
        var shown = Phase == SplashPhase.Ready ? _step.Total : _step.Index + 1;
        StepOf = _loc.Get("SplashStepOf", shown, _step.Total);
        DetailLine = Phase == SplashPhase.Running && _detail is not null ? _loc.Get("SplashNow", _detail) : null;
        SlowSuffix = Phase == SplashPhase.Running && IsSlow ? " " + _loc["SplashSlower"] : string.Empty;

        switch (Phase)
        {
            case SplashPhase.Ready:
                Title = _loc["SplashReady"];
                SubLine = _loc.Get("SplashReadySub", (Clock.GetUtcNow() - _runStartedAt).TotalSeconds.ToString("0.0"));
                AccessibleStatus = _loc["SplashA11yReady"];
                break;
            case SplashPhase.Failed:
                Title = _loc["SplashFailed" + Capitalize(key)];
                SubLine = _loc[StartupSteps.HasDegradedMode(key) ? "SplashFailedPluginsMeans" : "SplashFailedOtherMeans"];
                AccessibleStatus = $"{Title}. {ErrorText}";
                break;
            default:
                Title = stepTitle;
                SubLine = IsSlow
                    ? _loc.Get("SplashWaited", WaitedSeconds, _loc["SplashWhy" + Capitalize(key)])
                    : _loc[StepKey(key) + "Sub"];
                AccessibleStatus = IsSlow
                    ? _loc.Get("SplashA11ySlow", stepTitle)
                    : _loc.Get("SplashA11yStep", _step.Index + 1, _step.Total, stepTitle);
                break;
        }

        var rows = BuildRows(_step);
        if (!rows.SequenceEqual(Rows))
        {
            // Replaced only when a row really changes: a new list re-creates the row visuals, which would
            // replay the drop-in animation on every one-second tick.
            Rows = rows;
        }
    }

    private List<TrayRow> BuildRows(StartupStep step)
    {
        var rows = new List<TrayRow>(step.Total);
        for (var i = step.Total - 1; i >= 0; i--) // top of the tray first; step 1 is the bottom row
        {
            var state = Phase switch
            {
                SplashPhase.Ready => TrayRowState.Done,
                _ when i < step.Index => TrayRowState.Done,
                _ when i > step.Index => TrayRowState.Empty,
                SplashPhase.Failed => TrayRowState.Failed,
                _ => IsSlow ? TrayRowState.Slow : TrayRowState.Active,
            };
            var isPlugins = _keysByIndex.TryGetValue(i, out var k) && k == StartupSteps.Plugins;
            rows.Add(new TrayRow(state, isPlugins));
        }

        return rows;
    }

    private static string StepKey(string key) => "SplashStep" + Capitalize(key);

    private static string Capitalize(string key) => char.ToUpperInvariant(key[0]) + key[1..];
}
