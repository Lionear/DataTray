using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DataTray.App.Controls;

namespace DataTray.App.Views;

/// <summary>
/// A standalone, non-modal viewer for a single result-grid cell (SE-178 follow-up): opened by double-clicking
/// a cell so a long text / JSON value can be read comfortably, JSON pretty-printed. Several can be open at once
/// for side-by-side comparison — that's why it's a plain <see cref="Window"/> shown non-modally, not a dialog,
/// and each new one cascades a little so they don't stack exactly on top of one another.
/// </summary>
public partial class CellValueWindow : Window
{
    private static int _cascade;
    private readonly int _cascadeIndex = _cascade++;
    private readonly string _value = string.Empty;
    private readonly string _copiedLabel = "Copied";

    public CellValueWindow() => InitializeComponent();

    public CellValueWindow(string title, string value, string copyLabel, string copiedLabel)
    {
        InitializeComponent();
        _value = value;
        _copiedLabel = copiedLabel;
        Title = title;
        ValueBox.Text = value;
        CopyButton.Content = copyLabel;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Cascade each successive viewer down-right from the owner so multiple opens are visible at once.
        if (Owner is Window owner)
        {
            var step = _cascadeIndex % 8 * 28;
            Position = new PixelPoint(owner.Position.X + 120 + step, owner.Position.Y + 120 + step);
        }
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e) =>
        await CopyFeedback.CopyAsync(this, _value, _copiedLabel);
}
