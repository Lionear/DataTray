using Avalonia.Controls;

namespace DataTray.Providers.MsSql;

/// <summary>
/// One page of the Agent job dialog. The dialog owns the footer bar, so a page says what its primary action
/// is called rather than growing a Save button of its own at the end of a form — where it used to sit below
/// the fold on the longer pages.
/// </summary>
internal interface IJobPage
{
    Control Control { get; }

    /// <summary>Label for the footer's primary button, or null for a page with nothing to save.</summary>
    string? ActionLabel { get; }

    Task SaveAsync();
}
