namespace DataTray.App.Startup;

/// <summary>The work behind one step. The progress takes a short detail ("Tools · 2 of 5") and may be
/// reported from any thread.</summary>
public sealed record StartupWork(string Key, Func<IProgress<string>, Task> RunAsync);
