using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// The confirmation in front of KILL. The host's own destructive-action confirm belongs to a tool <i>run</i>
/// and a document tab never has one, so the monitor asks for itself — and it names the session's login and
/// host in the question, because a bare session id is not something anyone can check before agreeing to it.
/// </summary>
internal static class ConfirmKillWindow
{
    public static async Task<bool> ShowAsync(Window owner, IPluginLocalizer loc, int sessionId, string login, string host)
    {
        var window = new Window
        {
            Title = loc.Get("activity.kill.title"),
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        var cancel = new Button { Content = loc.Get("activity.kill.cancel"), IsCancel = true };
        cancel.Click += (_, _) => window.Close(false);

        var confirm = new Button { Content = loc.Get("activity.kill.confirm"), IsDefault = true };
        confirm.Click += (_, _) => window.Close(true);

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            MaxWidth = 420,
            Children =
            {
                new TextBlock
                {
                    Text = loc.Get("activity.kill.question", sessionId, login, host),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = loc.Get("activity.kill.warning"),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Opacity = 0.75
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, confirm }
                }
            }
        };

        return await window.ShowDialog<bool>(owner);
    }
}
