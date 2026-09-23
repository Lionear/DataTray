using Avalonia.Controls;
using DataTray.App.Views;

namespace DataTray.App.Tests;

public class TitleBarDoubleTapTests
{
    [Fact] // SE-291: with our own title bar the window manager never sees the click, so the app has to do
           // the maximise itself — except where the platform still does it, and then it must not undo it.
    public void Resolve_toggles_only_when_the_platform_left_the_window_alone()
    {
        // Windows/Linux: nothing moved between the tap and the check, so this handler is the only one acting.
        Assert.Equal(WindowState.Maximized, TitleBarDoubleTap.Resolve(WindowState.Normal, WindowState.Normal));
        Assert.Equal(WindowState.Normal, TitleBarDoubleTap.Resolve(WindowState.Maximized, WindowState.Maximized));

        // macOS: the platform zoomed on its own. Toggling back would read as a double-click that does nothing.
        Assert.Null(TitleBarDoubleTap.Resolve(WindowState.Normal, WindowState.Maximized));
        Assert.Null(TitleBarDoubleTap.Resolve(WindowState.Maximized, WindowState.Normal));

        // Full screen is left to the platform's own gesture; minimised has no title bar to hit.
        Assert.Null(TitleBarDoubleTap.Resolve(WindowState.FullScreen, WindowState.FullScreen));
        Assert.Null(TitleBarDoubleTap.Resolve(WindowState.Minimized, WindowState.Minimized));
    }
}
