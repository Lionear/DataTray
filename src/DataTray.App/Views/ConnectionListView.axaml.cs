using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DataTray.App.ViewModels;

namespace DataTray.App.Views;

/// <summary>
/// The connection/folder tree, hosted in the main window's sidebar (SE-289 — it used to be the left
/// half of the Connection Manager window). Everything below is the window's drag &amp; drop code
/// unchanged; only the confirmation dialog had to learn to find its owner window instead of being one.
/// </summary>
public partial class ConnectionListView : UserControl
{
    private const double DragThreshold = 5;

    private readonly TreeView _tree;

    // The pointer-press that may turn into a drag: its position, the node under it, and the original
    // args (DoDragDropAsync needs the PointerPressedEventArgs to capture the pointer).
    private Point _pressPoint;
    private ConnectionManagerNode? _pressNode;
    private PointerPressedEventArgs? _pressArgs;

    // The node currently being dragged, and the rows currently showing a drop-highlight or insert line.
    private ConnectionManagerNode? _dragged;
    private ConnectionManagerNode? _highlighted;
    private ConnectionManagerNode? _insertHint;

    public ConnectionListView()
    {
        InitializeComponent();

        _tree = this.FindControl<TreeView>("ConnectionTree")!;
        DragDrop.SetAllowDrop(_tree, true);
        _tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        _tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
        DragDrop.AddDragOverHandler(_tree, OnDragOver);
        DragDrop.AddDropHandler(_tree, OnDrop);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConnectionManagerViewModel vm)
            {
                vm.ConfirmRequested = ShowConfirmAsync;
            }
        };
    }

    private ConnectionManagerViewModel? ViewModel => DataContext as ConnectionManagerViewModel;

    private async Task<bool> ShowConfirmAsync(string title, string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }

        var loc = ViewModel?.Loc;
        var dialog = new ConfirmDialog(title, message, loc?["Yes"] ?? "Yes", loc?["No"] ?? "No");
        return await dialog.ShowDialog<bool>(owner);
    }

    // --- Drag & drop: reparent a connection/folder by dropping it onto a folder (or the root). ---

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressNode = NodeFrom(e.Source);
        _pressPoint = e.GetPosition(_tree);
        _pressArgs = e;
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressNode is null || _pressArgs is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed)
        {
            _pressNode = null;
            _pressArgs = null;
            return;
        }

        var delta = e.GetPosition(_tree) - _pressPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        _dragged = _pressNode;
        var trigger = _pressArgs;
        _pressNode = null;
        _pressArgs = null;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(_dragged.Name));
        try
        {
            await DragDrop.DoDragDropAsync(trigger, transfer, DragDropEffects.Move);
        }
        finally
        {
            ClearHighlight();
            _dragged = null;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var (target, position) = ResolveDrop(e);
        UpdateHint(target, position);

        var allowed = _dragged is not null && ViewModel?.CanDrop(_dragged, target, position) == true;
        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var (target, position) = ResolveDrop(e);
        if (_dragged is not null)
        {
            ViewModel?.Drop(_dragged, target, position);
        }

        ClearHighlight();
        e.Handled = true;
    }

    // Resolve the row under the pointer and where the drop lands within it: top third = Before, middle
    // third = Inside (reparent, only meaningful for folders and root), bottom third = After. A hover with
    // no row = root drop (Inside null).
    private (ConnectionManagerNode? Target, DropPosition Position) ResolveDrop(DragEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<TreeViewItem>() is not { } row
            || row.DataContext is not ConnectionManagerNode hovered)
        {
            return (null, DropPosition.Inside);
        }

        var relY = e.GetPosition(row).Y;
        var height = row.Bounds.Height;
        if (height <= 0)
        {
            return (hovered, DropPosition.Inside);
        }

        var third = height / 3.0;
        var position = relY < third
            ? DropPosition.Before
            : relY > height - third
                ? DropPosition.After
                : hovered.IsFolder ? DropPosition.Inside : DropPosition.After;
        return (hovered, position);
    }

    private void UpdateHint(ConnectionManagerNode? target, DropPosition position)
    {
        ClearHighlight();
        if (target is null)
        {
            return;
        }

        if (position == DropPosition.Inside && target.IsFolder)
        {
            target.IsDropTarget = true;
            _highlighted = target;
        }
        else if (position == DropPosition.Before)
        {
            target.IsInsertBefore = true;
            _insertHint = target;
        }
        else if (position == DropPosition.After)
        {
            target.IsInsertAfter = true;
            _insertHint = target;
        }
    }

    private void ClearHighlight()
    {
        if (_highlighted is not null)
        {
            _highlighted.IsDropTarget = false;
            _highlighted = null;
        }

        if (_insertHint is not null)
        {
            _insertHint.IsInsertBefore = false;
            _insertHint.IsInsertAfter = false;
            _insertHint = null;
        }
    }

    private static ConnectionManagerNode? NodeFrom(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>()?.DataContext as ConnectionManagerNode;
}
