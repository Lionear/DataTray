namespace DataTray.App.Startup;

/// <summary>
/// Debug builds only: <c>DATATRAY_SPLASH_SIMULATE=slow|fail|fail-workspace</c> makes a step slow or throw, so
/// every splash state can be seen in the real app. A release build ignores the variable.
/// </summary>
internal sealed class StartupSimulation(string? mode)
{
    public static StartupSimulation Current { get; } = new(
#if DEBUG
        Environment.GetEnvironmentVariable("DATATRAY_SPLASH_SIMULATE")
#else
        null
#endif
    );

    /// <summary>Runs inside the step's own work: for plugins that is the worker thread, so "slow" blocks it
    /// the way a slow plugin load would, while the splash keeps drawing.</summary>
    public void During(string step, bool bundledPluginsOnly)
    {
        // "Continue without store plugins" must get past a simulated plugin problem, as it would a real one.
        var plugins = step == StartupSteps.Plugins && !bundledPluginsOnly;
        switch (mode)
        {
            case "slow" when plugins:
                Thread.Sleep(TimeSpan.FromSeconds(35));
                break;
            case "fail" when plugins:
                throw new IOException("Simulated failure: access to the path 'plugins/acme' is denied.");
            case "fail-workspace" when step == StartupSteps.Workspace:
                throw new InvalidOperationException("Simulated failure: open-tabs.json could not be read.");
        }
    }
}
