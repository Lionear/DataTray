using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DataTray.App.ViewModels;

/// <summary>One row of Settings ▸ Toolbar: an action, whether it is shown, and — for a plugin-contributed
/// one — the owning plugin as a muted suffix. Order is the row's position in the list.</summary>
public partial class ToolbarSettingItem(string id, string label, string? pluginTitle, Geometry? icon, bool shown)
    : ObservableObject
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public string? PluginTitle { get; } = pluginTitle;

    public Geometry? Icon { get; } = icon;

    [ObservableProperty]
    private bool _isShown = shown;
}
