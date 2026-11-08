namespace DataTray.App.Startup;

/// <summary>One startup step as the splash shows it: which step (<see cref="StartupSteps"/>), and where it sits
/// in this run. Its status lives on the splash, which owns the clock that decides "slow".</summary>
public sealed record StartupStep(string Key, int Index, int Total);
