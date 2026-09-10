using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// The whole statement behind a row of the two query grids, as the server wrote it. The grids collapse a
/// query onto one line and clip it to a column, which recognises a statement but cannot read one — so a
/// double-click puts it here instead: monospaced, selectable, and with a Copy button for the common next
/// step of pasting it into an editor.
/// </summary>
internal static class QueryTextWindow
{
    public static async Task ShowAsync(Window owner, IPluginLocalizer loc, string sql)
    {
        var window = new Window
        {
            Title = loc.Get("activity.query.title"),
            Width = 760,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        // A read-only TextBox rather than a TextBlock: it selects, it scrolls, and Ctrl+C works without the
        // button — the same shape the host's own cell viewer uses.
        var text = new TextBox
        {
            Text = sql,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
            FontSize = 12
        };

        var copy = new Button { Content = loc.Get("activity.query.copy") };
        copy.Click += async (_, _) =>
        {
            if (window.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(sql);
            }
        };

        var close = new Button { Content = loc.Get("activity.query.close"), IsCancel = true, IsDefault = true };
        close.Click += (_, _) => window.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { copy, close }
        };

        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        layout.Children.Add(text);
        window.Content = layout;

        await window.ShowDialog(owner);
    }
}
