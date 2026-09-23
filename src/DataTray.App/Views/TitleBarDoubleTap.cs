using Avalonia.Controls;

namespace DataTray.App.Views;

/// <summary>
/// What a double-click on our own title bar should do (SE-291).
/// <para>
/// The interesting case is not the toggle, it is the one where somebody else already did it. With the
/// client area extended and the decorations reduced to a border, the window manager never sees a click on
/// non-client area, so on Windows and Linux nothing happens unless the app acts. On macOS the decorations
/// stay Full and the platform still zooms on a double-click — a handler that toggled unconditionally would
/// undo that, which looks exactly like a double-click that does nothing.
/// </para>
/// </summary>
internal static class TitleBarDoubleTap
{
    /// <summary>The state to move to, or null to leave the window alone.</summary>
    /// <param name="atTap">The state when the double-click arrived.</param>
    /// <param name="now">
    /// The state a moment later. Different from <paramref name="atTap"/> means the platform handled the
    /// click itself, and the answer is to do nothing rather than to toggle it back.
    /// </param>
    public static WindowState? Resolve(WindowState atTap, WindowState now)
    {
        if (now != atTap)
        {
            return null;
        }

        return atTap switch
        {
            WindowState.Maximized => WindowState.Normal,

            // Minimised cannot be double-clicked — there is no title bar on screen to hit. Full screen can
            // be, and does nothing on purpose: leaving full screen is the platform's own gesture (the green
            // button, ⌃⌘F, Escape), not one this app invents for the title bar.
            WindowState.Minimized or WindowState.FullScreen => null,

            _ => WindowState.Maximized,
        };
    }
}
