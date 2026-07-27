using System.Collections.ObjectModel;
using Avalonia.Media;
using DataTray.Core.Connections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DataTray.App.ViewModels;

/// <summary>
/// One row in the Connection Manager's left tree. Deliberately light — unlike the sidebar's
/// <see cref="TreeNodeViewModel"/> it never loads a schema and has a first-class "folder" concept that
/// can exist without any connection under it. A node is either a folder (holds sub-folders +
/// connections) or a saved connection (a leaf).
/// </summary>
public partial class ConnectionManagerNode : ObservableObject
{
    // Folder node.
    private ConnectionManagerNode(string name, string folderPath)
    {
        IsFolder = true;
        Name = name;
        FolderPath = folderPath;
    }

    // Connection node.
    private ConnectionManagerNode(SavedConnection connection, IImage? iconImage)
    {
        Connection = connection;
        Name = connection.Name;
        IconImage = iconImage;
    }

    public static ConnectionManagerNode ForFolder(string name, string fullPath) => new(name, fullPath);

    public static ConnectionManagerNode ForConnection(SavedConnection connection, IImage? iconImage = null) =>
        new(connection, iconImage);

    public bool IsFolder { get; }

    public bool IsConnection => !IsFolder;

    /// <summary>Folder segment name, or the connection's name.</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>Full /-joined path of a folder node (e.g. "Klanten/Klant A"); null for connections.</summary>
    public string? FolderPath { get; private set; }

    /// <summary>The saved connection for a leaf node; null for folders.</summary>
    public SavedConnection? Connection { get; }

    public ObservableCollection<ConnectionManagerNode> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>Drives the drop-target highlight while a drag hovers over this folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DropBrush))]
    private bool _isDropTarget;

    /// <summary>Shows a thin insert-line above this row while a drag would land immediately before it.</summary>
    [ObservableProperty]
    private bool _isInsertBefore;

    /// <summary>Shows a thin insert-line below this row while a drag would land immediately after it.</summary>
    [ObservableProperty]
    private bool _isInsertAfter;

    // Row background while it's a valid drop target (translucent accent), else transparent.
    private static readonly IBrush DropHighlight = new SolidColorBrush(Color.Parse("#333574F0"));

    /// <summary>Row background: a translucent accent while this folder is a drop target, else transparent.</summary>
    public IBrush DropBrush => IsDropTarget ? DropHighlight : Brushes.Transparent;

    /// <summary>Number of connections nested anywhere under a folder (recursive); shown as a count badge.</summary>
    public int ConnectionCount => Children.Sum(c => c.IsFolder ? c.ConnectionCount : 1);

    /// <summary>Accent brush for a colour-flagged connection (prod = red); null when unset/invalid or a folder.</summary>
    public IBrush? ColorBrush =>
        Connection?.Color is { } hex && Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : null;

    public bool HasColor => ColorBrush is not null;

    /// <summary>Line-icon for the row: a folder glyph or a generic connection glyph (see <see cref="NodeIcons"/>).</summary>
    public Geometry IconGeometry => IsFolder ? NodeIcons.Folder : NodeIcons.Connection;

    /// <summary>Provider brand icon for connection rows (null for folders or when the provider ships no image).</summary>
    public IImage? IconImage { get; }

    public bool HasImageIcon => IconImage is not null;

    public bool HasVectorIcon => IconImage is null;

    /// <summary>Re-path a folder node after a rename/move (its full path and display segment change).</summary>
    public void Relocate(string name, string fullPath)
    {
        Name = name;
        FolderPath = fullPath;
    }
}
