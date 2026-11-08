using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DataTray.Core.Plugins;

namespace DataTray.App.ViewModels;

/// <summary>One plugin pane offered in a query tab: its toggle, and the control while it is open.</summary>
public partial class QueryPaneSlot(SubsystemQueryPane pane) : ObservableObject
{
    public SubsystemQueryPane Pane { get; } = pane;

    public string Title { get; } = pane.Plugin.Title;

    public Geometry? Icon { get; } = pane.Plugin.Icon;

    /// <summary>False while the plugin's <c>AppliesTo</c> rejects the tab; the toggle is hidden then.</summary>
    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private Control? _control;

    /// <summary>Why the pane could not be built, shown in its place.</summary>
    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private double _width = 360;

    /// <summary>The plugin's shortcut, parsed; null when it has none or it is malformed.</summary>
    public KeyGesture? Gesture { get; internal set; }

    internal QueryPaneContext? Context { get; set; }
}
