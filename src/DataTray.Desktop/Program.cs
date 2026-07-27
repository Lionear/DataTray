using System;
using System.Linq;
using Avalonia;

namespace DataTray.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // FIRST, before anything touches the app data root (SE-206). The rename moved that root from
        // Lionear/SqlExplorer to Lionear/DataTray, and the migration only copies when the new root does
        // not exist yet — so anything that creates it first makes the migration a silent no-op and the
        // user starts with an empty app. RestartDiagnostics.Log below is exactly such a thing: it calls
        // Directory.CreateDirectory on the root before writing a line. Keep this call above it.
        var migration = DataTray.Core.Migration.AppDataMigration.Migrate();

        // Strictly after the copy above (it rewrites files inside the new root) and before anything reads
        // plugin state, which AppServices does while building the container.
        var pluginIds = DataTray.Core.Migration.PluginIdMigration.Migrate();

        // A relaunch (Restart-app button / in-app updater) must always take over the UI. Skip the
        // single-instance probe when relaunched, so the new instance doesn't connect to the old one's pipe
        // (still open while it shuts down), defer to it and exit — which left no window at all (SE-125).
        var relaunched = args.Contains(DataTray.App.AppRestart.RelaunchArgument);
        DataTray.App.RestartDiagnostics.Log(
            $"start: relaunched={relaunched} argv=[{string.Join(' ', args)}]");

        if (migration.Outcome != DataTray.Core.Migration.AppDataMigrationOutcome.NothingToDo)
        {
            DataTray.App.RestartDiagnostics.Log(migration.Error is { } error
                ? $"start: app-data migration {migration.Outcome} — {error.GetType().Name}: {error.Message}"
                : $"start: app-data migration {migration.Outcome} ({migration.FilesCopied} file(s))");
        }

        if (pluginIds.DidAnything)
        {
            DataTray.App.RestartDiagnostics.Log(
                $"start: plugin-id migration rewrote {string.Join(", ", pluginIds.Changed)}");
        }

        // Single instance: if the app is already running (possibly hidden in the tray), tell it to surface
        // its window and exit — don't open a second copy. The primary's listener is started in App.
        // Skipped when the user opted into multiple instances (SE-124), so a second launch opens its own
        // window. Read straight from the settings file here, before Avalonia/DI exist.
        var allowMultiple = DataTray.App.SingleInstance.MultipleInstancesAllowed();
        if (!relaunched && !allowMultiple && !DataTray.App.SingleInstance.TryBecomePrimary())
        {
            DataTray.App.RestartDiagnostics.Log("start: deferred to existing primary — exiting");
            return;
        }

        if (allowMultiple)
        {
            DataTray.App.RestartDiagnostics.Log("start: multiple-instances allowed — skipping single-instance probe");
        }

        DataTray.App.RestartDiagnostics.Log("start: becoming primary — launching UI");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DataTray.App.App>()
            .UsePlatformDetect()
            .LogToTrace();
}
