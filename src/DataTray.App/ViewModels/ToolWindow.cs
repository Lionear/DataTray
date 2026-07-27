using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DataTray.App.ViewModels;

/// <summary>Which edge of the main window a tool-window docks against. Drives where its toggle lives:
/// <see cref="Right"/> → the vertical stripe on the right; <see cref="Bottom"/> → the status bar.</summary>
public enum ToolWindowEdge
{
    Bottom,
    Right
}

/// <summary>
/// One dockable tool-window (Output, History, and later Query Log / Activity / MCP status). The panel
/// declares its edge, title, icon and optional badge; the host renders the toggle on the matching edge,
/// the splitter and the size persistence from that. This is the seam SE-123 introduces so panel #3 costs
/// no new plumbing — the rule "new capability via the existing extension pattern, never a separate system
/// alongside it" applied to the main window.
/// </summary>
public sealed partial class ToolWindow : ObservableObject
{
    public ToolWindow(string id, ToolWindowEdge edge, string title, Geometry icon, double defaultSize)
    {
        Id = id;
        Edge = edge;
        Title = title;
        Icon = icon;
        _size = defaultSize;
    }

    /// <summary>Stable key used to persist size/visibility; matches the settings field names.</summary>
    public string Id { get; }

    public ToolWindowEdge Edge { get; }

    /// <summary>Localized display title — shown in the stripe button and the panel header.</summary>
    [ObservableProperty]
    private string _title;

    /// <summary>Vector line-icon (see <see cref="NodeIcons"/>); never an emoji or icon-font glyph.</summary>
    public Geometry Icon { get; }

    /// <summary>Panel height (Bottom) or width (Right) in pixels; two-way bound to the splitter and
    /// persisted so a resize survives a restart.</summary>
    [ObservableProperty]
    private double _size;

    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Optional count shown on the toggle — for Output it's the number of errors currently in
    /// the log, so a failure is visible without opening the panel. Null/0 = no badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    private int? _badge;

    public bool HasBadge => Badge is > 0;

    /// <summary>Whether this window's toggle is offered at all. Normally true; the first-party AI-activity
    /// panel sets it false while the MCP server is stopped, so its toggle disappears (SE-183).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBottomToggle))]
    [NotifyPropertyChangedFor(nameof(IsRightToggle))]
    private bool _isAvailable = true;

    /// <summary>Show the status-bar toggle (Bottom-edge windows) — gated on <see cref="IsAvailable"/>.</summary>
    public bool IsBottomToggle => Edge == ToolWindowEdge.Bottom && IsAvailable;

    /// <summary>Show the right-stripe toggle (Right-edge windows) — gated on <see cref="IsAvailable"/>.</summary>
    public bool IsRightToggle => Edge == ToolWindowEdge.Right && IsAvailable;
}
