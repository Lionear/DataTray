namespace DataTray.App.Startup;

/// <summary>When the splash calls a step slow, and when it starts offering a way past it.</summary>
public static class StartupThresholds
{
    public static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan WaysOutAfter = TimeSpan.FromSeconds(30);
}
