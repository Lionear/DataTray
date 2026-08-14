using System.Windows.Input;
using Avalonia.Media;
using DataTray.Core.Toolbar;

namespace DataTray.App.ViewModels;

/// <summary>
/// One resolved application-toolbar action as the strip renders it: a catalog entry (<see cref="Id"/>)
/// bound to the command and icon the host or the owning plugin supplies. Immutable — the strip is rebuilt
/// wholesale whenever the layout changes, which is rare and cheap.
/// </summary>
public sealed class ToolbarActionViewModel(
    string id,
    string label,
    Geometry? icon,
    ICommand command,
    bool isAccent = false,
    string? detail = null)
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public Geometry? Icon { get; } = icon;

    public ICommand Command { get; } = command;

    /// <summary>The primary action's filled styling ("New query tab").</summary>
    public bool IsAccent { get; } = isAccent;

    /// <summary>Muted suffix in the overflow flyout: a shortcut hint, or the owning plugin's name.</summary>
    public string? Detail { get; } = detail;

    /// <summary>
    /// The quick-open renders as a search field rather than a button while it is in the strip; in the
    /// overflow flyout it is an ordinary row that opens the same quick-open. One special case in the item
    /// template, none in the layout model.
    /// </summary>
    public bool IsQuickOpen => Id == ToolbarCatalog.Ids.GoToObject;

    public bool IsButton => !IsQuickOpen;
}
