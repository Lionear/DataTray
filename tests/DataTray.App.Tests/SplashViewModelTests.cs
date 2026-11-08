using System.Globalization;
using DataTray.App.Localization;
using DataTray.App.Startup;
using DataTray.App.ViewModels;

namespace DataTray.App.Tests;

public class SplashViewModelTests
{
    private readonly FakeClock _clock = new();
    private readonly SplashViewModel _vm;

    public SplashViewModelTests()
    {
        var loc = new ResxLocalizer();
        loc.SetCulture(CultureInfo.GetCultureInfo("en"));
        _vm = new SplashViewModel(loc, _clock);
    }

    [Fact]
    public void A_reported_step_runs_and_names_its_position()
    {
        _vm.Report(new StartupStep(StartupSteps.Workspace, 1, 3));

        Assert.Equal(SplashPhase.Running, _vm.Phase);
        Assert.Equal("Putting your workspace back", _vm.Title);
        Assert.Equal("2 of 3", _vm.StepOf);
        Assert.Equal("Step 2 of 3: Putting your workspace back", _vm.AccessibleStatus);
    }

    [Fact] // The tray is drawn top-down, so the first step is the bottom row.
    public void Rows_fill_the_tray_from_the_bottom()
    {
        _vm.Report(new StartupStep(StartupSteps.Workspace, 1, 3));

        Assert.Equal([TrayRowState.Empty, TrayRowState.Active, TrayRowState.Done], _vm.Rows.Select(r => r.State));
    }

    [Theory]
    [InlineData(4.9, false, false)]
    [InlineData(5.0, true, false)]
    [InlineData(29.9, true, false)]
    [InlineData(30.0, true, true)]
    public void A_step_turns_slow_after_five_seconds_and_offers_ways_out_after_thirty(
        double seconds, bool slow, bool waysOut)
    {
        _vm.Report(new StartupStep(StartupSteps.Plugins, 0, 3));

        _clock.Advance(TimeSpan.FromSeconds(seconds));
        _vm.Tick();

        Assert.Equal(slow, _vm.IsSlow);
        Assert.Equal(waysOut, _vm.ShowWaysOut);
    }

    [Fact]
    public void A_slow_step_says_how_long_it_waited_and_why()
    {
        _vm.Report(new StartupStep(StartupSteps.Plugins, 0, 3));

        _clock.Advance(TimeSpan.FromSeconds(9.7));
        _vm.Tick();

        Assert.Equal(9, _vm.WaitedSeconds);
        Assert.Equal(" (slower than usual)", _vm.SlowSuffix); // drawn in the waiting colour after the title
        Assert.Equal("9 s so far. DataTray can't open a connection without its engines.", _vm.SubLine);
        Assert.Equal(TrayRowState.Slow, _vm.Rows[^1].State);
    }

    [Fact] // Only the plugin steps have a safe degraded mode; anywhere else "continue without" means nothing.
    public void A_step_without_a_degraded_mode_never_offers_ways_out()
    {
        _vm.Report(new StartupStep(StartupSteps.Workspace, 1, 3));

        _clock.Advance(TimeSpan.FromSeconds(31));
        _vm.Tick();

        Assert.True(_vm.IsSlow);
        Assert.False(_vm.ShowWaysOut);
    }

    [Fact]
    public void The_next_step_starts_its_own_wait()
    {
        _vm.Report(new StartupStep(StartupSteps.Plugins, 0, 3));
        _clock.Advance(TimeSpan.FromSeconds(12));
        _vm.Tick();

        _vm.Report(new StartupStep(StartupSteps.Workspace, 1, 3));
        _vm.Tick();

        Assert.False(_vm.IsSlow);
        Assert.Equal(0, _vm.WaitedSeconds);
    }

    [Fact] // A detail is progress inside the same step: it must not reset the clock that decides "slow".
    public void A_detail_updates_the_line_without_restarting_the_wait()
    {
        _vm.Report(new StartupStep(StartupSteps.Plugins, 0, 3));
        _clock.Advance(TimeSpan.FromSeconds(6));

        _vm.ReportDetail("Tools · 2 of 5");
        _vm.Tick();

        Assert.True(_vm.IsSlow);
        Assert.Equal("Now: Tools · 2 of 5", _vm.DetailLine);
    }

    [Theory]
    [InlineData(StartupSteps.Updates, true)]
    [InlineData(StartupSteps.Plugins, true)]
    [InlineData(StartupSteps.Workspace, false)]
    [InlineData(StartupSteps.Extensions, false)]
    public void A_failed_step_shows_the_error_and_only_plugin_steps_can_continue_without(string key, bool canContinue)
    {
        _vm.Report(new StartupStep(key, 0, 3));

        _vm.Fail(new IOException("Access to the path '/x/plugins/redis' is denied."));

        Assert.Equal(SplashPhase.Failed, _vm.Phase);
        Assert.Equal("IOException: Access to the path '/x/plugins/redis' is denied.", _vm.ErrorText);
        Assert.Equal(canContinue, _vm.CanContinueWithout);
        Assert.Equal(TrayRowState.Failed, _vm.Rows[^1].State);
    }

    [Fact]
    public void Ready_fills_every_row_and_says_how_long_the_start_took()
    {
        _vm.Report(new StartupStep(StartupSteps.Plugins, 0, 3));
        _clock.Advance(TimeSpan.FromSeconds(2.4));

        _vm.Complete();

        Assert.Equal(SplashPhase.Ready, _vm.Phase);
        Assert.All(_vm.Rows, r => Assert.Equal(TrayRowState.Done, r.State));
        Assert.Equal($"Started in {2.4:0.0} s", _vm.SubLine); // numbers follow the machine format (SE-276)
        Assert.Equal("DataTray is ready", _vm.AccessibleStatus);
    }

    [Fact] // Continue-without re-runs the sequence on the same window: a new report clears the failure.
    public void Reporting_again_after_a_failure_runs_again()
    {
        _vm.Report(new StartupStep(StartupSteps.Plugins, 0, 3));
        _vm.Fail(new InvalidOperationException("boom"));

        _vm.Report(new StartupStep(StartupSteps.Plugins, 0, 3));

        Assert.Equal(SplashPhase.Running, _vm.Phase);
        Assert.Null(_vm.ErrorText);
    }

    [Fact]
    public void The_failure_actions_reach_their_handlers()
    {
        var called = new List<string>();
        _vm.RetryRequested = () => called.Add("retry");
        _vm.ContinueWithoutRequested = () => called.Add("continue");
        _vm.OpenLogRequested = () => called.Add("log");
        _vm.QuitRequested = () => called.Add("quit");

        _vm.RetryCommand.Execute(null);
        _vm.ContinueWithoutCommand.Execute(null);
        _vm.OpenLogCommand.Execute(null);
        _vm.QuitCommand.Execute(null);

        Assert.Equal(["retry", "continue", "log", "quit"], called);
    }
}
