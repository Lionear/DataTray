using Avalonia;
using Avalonia.Styling;
using DataTray.Core.Settings;

namespace DataTray.App.Theming;

/// <summary>
/// Applies a saved <see cref="AppTheme"/> to the running app. Takes effect immediately — Avalonia
/// re-themes every open window/control the moment <c>RequestedThemeVariant</c> changes, no restart.
/// </summary>
public static class ThemeApplier
{
    public static void Apply(AppTheme theme)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default // "System": Avalonia then follows the OS via ActualThemeVariant.
        };
    }

    /// <summary>
    /// Forces every already-realized control in the app to re-evaluate its bindings. A plain
    /// <c>INotifyPropertyChanged.PropertyChanged(this, new(null))</c> (what <c>ILocalizer.SetCulture</c>
    /// raises) is the correct "everything on this object changed" signal, but empirically it does not,
    /// by itself, cause a repaint of controls that were already laid out before the change (confirmed:
    /// the bound value updates correctly, the on-screen text does not, with no amount of extra
    /// dispatcher pumping fixing it) — a <c>RequestedThemeVariant</c> change is the one thing observed
    /// to reliably cascade a full re-style/re-bind. This flips it away and immediately back so there is
    /// no net visual change, purely to piggyback on that cascade after a language switch.
    /// </summary>
    public static void ForceGlobalRefresh()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var current = app.RequestedThemeVariant;
        app.RequestedThemeVariant = current == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        app.RequestedThemeVariant = current;
    }
}
