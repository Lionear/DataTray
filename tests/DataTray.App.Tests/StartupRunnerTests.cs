using System.Globalization;
using DataTray.App.Localization;
using DataTray.App.Startup;
using DataTray.App.ViewModels;

namespace DataTray.App.Tests;

public class StartupRunnerTests
{
    private readonly FakeClock _clock = new();
    private readonly SplashViewModel _vm;
    private readonly List<string> _log = [];

    public StartupRunnerTests()
    {
        var loc = new ResxLocalizer();
        loc.SetCulture(CultureInfo.GetCultureInfo("en"));
        _vm = new SplashViewModel(loc, _clock);
    }

    [Fact]
    public async Task Every_step_is_reported_in_order_and_the_run_ends_ready()
    {
        var seen = new List<string>();
        var work = new[]
        {
            new StartupWork(StartupSteps.Plugins, _ => { seen.Add(_vm.StepOf); return Task.CompletedTask; }),
            new StartupWork(StartupSteps.Workspace, _ => { seen.Add(_vm.StepOf); return Task.CompletedTask; }),
            new StartupWork(StartupSteps.Extensions, _ => { seen.Add(_vm.StepOf); return Task.CompletedTask; }),
        };

        var ok = await StartupRunner.RunAsync(_vm, work, _log.Add);

        Assert.True(ok);
        Assert.Equal(["1 of 3", "2 of 3", "3 of 3"], seen);
        Assert.Equal(SplashPhase.Ready, _vm.Phase);
    }

    [Fact] // The reason the runner exists: a throwing step ends in the failure state, not in a crash.
    public async Task An_exception_in_a_step_lands_in_the_failure_state_and_stops_the_run()
    {
        var laterStepRan = false;
        var work = new[]
        {
            new StartupWork(StartupSteps.Plugins, _ => throw new IOException("disk gone")),
            new StartupWork(StartupSteps.Workspace, _ => { laterStepRan = true; return Task.CompletedTask; }),
        };

        var ok = await StartupRunner.RunAsync(_vm, work, _log.Add);

        Assert.False(ok);
        Assert.False(laterStepRan);
        Assert.Equal(SplashPhase.Failed, _vm.Phase);
        Assert.Equal("IOException: disk gone", _vm.ErrorText);
        Assert.Contains(_log, l => l.Contains("plugins") && l.Contains("IOException"));
    }

    [Fact] // Steps run on a worker report through IProgress, which lands on the splash.
    public async Task A_step_can_report_a_detail_while_it_runs()
    {
        string? during = null;
        var work = new[]
        {
            new StartupWork(StartupSteps.Plugins, progress =>
            {
                progress.Report("Engines · 1 of 5");
                during = _vm.DetailLine;
                return Task.CompletedTask;
            }),
        };

        await StartupRunner.RunAsync(_vm, work, _log.Add);

        Assert.Equal("Now: Engines · 1 of 5", during);
    }

    [Fact] // "Continue without" starts a second run while the slow first one is still going: the first must
           // then leave the splash alone, or it would report over the new run and open a second main window.
    public async Task A_superseded_run_stops_without_touching_the_splash()
    {
        using var superseded = new CancellationTokenSource();
        var laterStepRan = false;
        var work = new[]
        {
            new StartupWork(StartupSteps.Plugins, _ => { superseded.Cancel(); return Task.CompletedTask; }),
            new StartupWork(StartupSteps.Workspace, _ => { laterStepRan = true; return Task.CompletedTask; }),
        };

        var ok = await StartupRunner.RunAsync(_vm, work, _log.Add, superseded.Token);

        Assert.False(ok);
        Assert.False(laterStepRan);
        Assert.Equal(SplashPhase.Running, _vm.Phase);
        Assert.Equal("1 of 2", _vm.StepOf);
    }

    [Fact] // The durations are what the 5 s threshold will be tuned against.
    public async Task Each_step_logs_how_long_it_took()
    {
        var work = new[]
        {
            new StartupWork(StartupSteps.Plugins, _ =>
            {
                _clock.Advance(TimeSpan.FromMilliseconds(812));
                return Task.CompletedTask;
            }),
        };

        await StartupRunner.RunAsync(_vm, work, _log.Add);

        Assert.Contains("start: step plugins 812 ms", _log);
    }
}
