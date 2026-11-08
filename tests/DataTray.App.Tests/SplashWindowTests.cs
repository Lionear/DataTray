using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using DataTray.App.Localization;
using DataTray.App.Startup;
using DataTray.App.ViewModels;
using DataTray.App.Views;

namespace DataTray.App.Tests;

public class SplashWindowTests : IClassFixture<HeadlessDrawing>
{
    private readonly SplashViewModel _vm;

    public SplashWindowTests()
    {
        var loc = new ResxLocalizer();
        loc.SetCulture(CultureInfo.GetCultureInfo("en"));
        _vm = new SplashViewModel(loc, new FakeClock());
    }

    [Theory] // Retry is the default action; "continue without" only where a degraded start exists.
    [InlineData(StartupSteps.Plugins, true)]
    [InlineData(StartupSteps.Workspace, false)]
    public void A_failed_step_shows_retry_as_default_and_continue_without_only_when_possible(string key, bool canContinue)
    {
        _vm.Report(new StartupStep(key, 0, 3));
        _vm.Fail(new IOException("disk gone"));

        var (retryShown, retryDefault, continueShown) = HeadlessDrawing.Run(() =>
        {
            var window = new SplashWindow(_vm, compact: false, reducedMotion: false);
            var retry = window.FindControl<Button>("RetryButton");
            return (retry?.IsVisible, retry?.IsDefault, window.FindControl<Button>("ContinueWithoutButton")?.IsVisible);
        });

        Assert.True(retryShown);
        Assert.True(retryDefault);
        Assert.Equal(canContinue, continueShown);
    }

    [Fact] // Screen readers get one live line that follows the progress, not the drawing.
    public void The_progress_is_a_polite_live_line_for_screen_readers()
    {
        _vm.Report(new StartupStep(StartupSteps.Plugins, 0, 3));

        var (live, statusName, windowName) = HeadlessDrawing.Run(() =>
        {
            var window = new SplashWindow(_vm, compact: false, reducedMotion: false);
            var status = window.FindControl<Control>("AccessibleStatus");
            return (status is null ? (AutomationLiveSetting?)null : AutomationProperties.GetLiveSetting(status),
                status is null ? null : AutomationProperties.GetName(status),
                AutomationProperties.GetName(window));
        });

        Assert.Equal(AutomationLiveSetting.Polite, live);
        Assert.Equal("Step 1 of 3: Loading your plugins", statusName);
        Assert.Equal("DataTray is starting", windowName);
    }

    [Theory] // Sizes come from styles: a local value on the window would silently beat the compact ones.
    [InlineData(false, 500)]
    [InlineData(true, 380)]
    public void The_compact_splash_is_narrower(bool compact, double width)
    {
        var actual = HeadlessDrawing.Run(() => new SplashWindow(_vm, compact, reducedMotion: false).Width);

        Assert.Equal(width, actual);
    }

    [Fact] // Reduced motion switches the animated styles off at the root.
    public void Reduced_motion_marks_the_window_so_no_animation_style_applies()
    {
        var classes = HeadlessDrawing.Run(() => new SplashWindow(_vm, compact: true, reducedMotion: true).Classes.ToList());

        Assert.Contains("reduced-motion", classes);
        Assert.Contains("compact", classes);
    }
}
