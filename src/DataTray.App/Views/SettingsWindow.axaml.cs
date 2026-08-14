using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DataTray.App.Controls;
using DataTray.App.ViewModels;

namespace DataTray.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.CloseRequested = Close;
                // Master-password prompts (Set/Change/Disable) — no inline validator; the service verifies.
                vm.PromptMasterPassword = mode =>
                    new MasterPasswordDialog(mode, vm.Loc, null).ShowDialog<MasterPasswordDialogResult?>(this);
                // "What's new" from the Updates pane opens the changelog dialog owned by this window.
                vm.ChangelogRequested = dialog => new UpdateAvailableWindow(dialog).ShowDialog(this);
                // Switching to a channel that is behind the running build (SE-163): say what it means and get
                // an explicit yes, rather than switching in silence — the "No" text is the safe default here.
                vm.ConfirmChannelDowngrade = async offer =>
                {
                    var dialog = new ConfirmDialog(
                        vm.Loc["UpdateChannelDowngradeTitle"],
                        vm.Loc.Get("UpdateChannelDowngradeMessage",
                            offer.Channel.ToString(), offer.Version, offer.RunningVersion),
                        vm.Loc["UpdateChannelDowngradeYes"],
                        vm.Loc["Cancel"]);
                    return await dialog.ShowDialog<bool>(this);
                };
            }
        };

        // Unsubscribe the VM from the MCP service singleton when the window closes (SE-147) — the transient VM
        // would otherwise leak a StateChanged handler on the long-lived service each time Settings opens.
        Closed += (_, _) => (DataContext as SettingsViewModel)?.Cleanup();
    }

    // --- Settings ▸ Toolbar reordering (SE-255) ----------------------------------------------------
    // Plain pointer handling on the grip rather than the drag-and-drop API: reordering inside one list is
    // a move, not a transfer, and this keeps the row's checkbox and label clickable as usual.

    private int _toolbarDragFrom = -1;

    private void OnToolbarGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _toolbarDragFrom = IndexOfRow(sender as Visual);
        if (_toolbarDragFrom >= 0)
        {
            e.Pointer.Capture(ToolbarList);
            e.Handled = true;
        }
    }

    private void OnToolbarListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_toolbarDragFrom < 0 || DataContext is not SettingsViewModel vm)
        {
            return;
        }

        var target = IndexOfRow(ToolbarList.InputHitTest(e.GetPosition(ToolbarList)) as Visual);
        if (target >= 0 && target != _toolbarDragFrom)
        {
            vm.MoveToolbarItem(_toolbarDragFrom, target);
            _toolbarDragFrom = target;
        }
    }

    private void OnToolbarListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _toolbarDragFrom = -1;
        e.Pointer.Capture(null);
    }

    // Keyboard equivalent of the drag handle, so reordering is not mouse-only.
    private void OnToolbarListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Alt || DataContext is not SettingsViewModel vm)
        {
            return;
        }

        var from = ToolbarList.SelectedIndex;
        var to = e.Key switch
        {
            Key.Up => from - 1,
            Key.Down => from + 1,
            _ => -1,
        };

        if (from < 0 || to < 0 || to >= vm.ToolbarItems.Count)
        {
            return;
        }

        vm.MoveToolbarItem(from, to);
        ToolbarList.SelectedIndex = to;
        e.Handled = true;
    }

    private int IndexOfRow(Visual? from)
    {
        while (from is not null and not ListBoxItem)
        {
            from = from.GetVisualParent();
        }

        return from is ListBoxItem row ? ToolbarList.IndexFromContainer(row) : -1;
    }

    // A File/Folder plugin setting: pick a path (a binary like mysqldump, or a default folder).
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginSettingFieldInput input })
        {
            return;
        }

        if (input.IsFolder)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            if (folders.Count > 0)
            {
                input.Value = folders[0].TryGetLocalPath() ?? folders[0].Path.ToString();
            }

            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (files.Count > 0)
        {
            input.Value = files[0].TryGetLocalPath() ?? files[0].Path.ToString();
        }
    }

    // Copy the MCP bearer token to the clipboard.
    private async void OnCopyMcpTokenClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { McpToken: { Length: > 0 } token } vm)
        {
            await CopyFeedback.CopyAsync(this, token, vm.Loc["CopiedToClipboard"]);
        }
    }

    // Copy the MCP server URL to the clipboard.
    private async void OnCopyMcpUrlClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { McpUrl: { Length: > 0 } url } vm)
        {
            await CopyFeedback.CopyAsync(this, url, vm.Loc["CopiedToClipboard"]);
        }
    }
}
