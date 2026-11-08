namespace DataTray.App.ViewModels;

/// <summary>One row in the splash's tray. The plugins row is drawn in the extensibility colour (SE-209).</summary>
public sealed record TrayRow(TrayRowState State, bool IsPlugins = false)
{
    public bool IsEmpty => State == TrayRowState.Empty;

    public bool IsActive => State == TrayRowState.Active;

    public bool IsSlow => State == TrayRowState.Slow;

    public bool IsDone => State == TrayRowState.Done && !IsPlugins;

    public bool IsDonePlugins => State == TrayRowState.Done && IsPlugins;

    public bool IsFailed => State == TrayRowState.Failed;
}
