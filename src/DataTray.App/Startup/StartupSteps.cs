namespace DataTray.App.Startup;

/// <summary>The real startup steps, in the order they run. <see cref="Updates"/> only runs when something is staged.</summary>
public static class StartupSteps
{
    public const string Updates = "updates";
    public const string Plugins = "plugins";
    public const string Workspace = "workspace";
    public const string Extensions = "extensions";

    /// <summary>Steps that can be skipped by starting with the bundled plugins only.</summary>
    public static bool HasDegradedMode(string key) => key is Updates or Plugins;
}
