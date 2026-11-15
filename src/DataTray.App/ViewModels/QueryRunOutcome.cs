namespace DataTray.App.ViewModels;

/// <summary>Where a query tab's last run stands, for the run clock and the status bar (SE-300).</summary>
public enum QueryRunOutcome
{
    None,
    Running,
    Completed,
    Cancelled,
    Failed,
}
