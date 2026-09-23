using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DataTray.App.ViewModels;
using DataTray.Core.Settings;
using DataTray.Core.Shortcuts;

namespace DataTray.App.Views;

public partial class MainWindow : Window
{
    private readonly IAppSettingsStore? _settingsStore;
    private readonly KeymapService? _keymap;

    // Parameterless ctor keeps the XAML previewer happy; the real app uses the injected overload.
    public MainWindow() : this(null, null)
    {
    }

    public MainWindow(IAppSettingsStore? settingsStore, KeymapService? keymap)
    {
        _settingsStore = settingsStore;
        _keymap = keymap;
        InitializeComponent();
        RestoreLayout();

        // Rebuild the window's key bindings whenever the user changes the keymap in Settings.
        if (_keymap is not null)
        {
            _keymap.Changed += RebuildKeyBindings;
        }

        // macOS gets its menu bar from NativeMenu.Menu (set in XAML) — the in-window Menu would
        // otherwise render a second, redundant bar underneath the title bar there.
        //
        // And the traffic lights stay (SE-291). They are a platform contract: the green one is macOS's
        // only route into full screen, Mission Control and Stage Manager expect them, and ⌃⌘F drives that
        // button rather than the window. So there the decorations stay Full — the client area is still
        // extended, we still draw the bar — and our own three buttons go away rather than sit beside a
        // second set.
        if (SystemCaptionButtons)
        {
            AppMenu.IsVisible = false;
            WindowDecorations = WindowDecorations.Full;
            CaptionButtons.IsVisible = false;

            // Our bar is taller than macOS's own 28 pt, so the traffic lights would sit high in it. AppKit
            // places them and Avalonia does not move them — an empty unified toolbar is what makes AppKit
            // centre them in the taller bar. Once the window exists: it needs the NSWindow.
            Opened += (_, _) => MacTitleBar.UseUnifiedTitleBar(this);
        }

        // Only the focused window's title bar is at full strength — with a title bar the app draws itself,
        // nothing else says which of two open windows is the one in front.
        Activated += (_, _) => TitleBarContent.Opacity = 1;
        Deactivated += (_, _) => TitleBarContent.Opacity = 0.55;

        // Keeps the middle button saying what it will do rather than what the window is.
        SyncMaximiseButton();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                SyncMaximiseButton();
            }
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                PopulateSubsystemMenu(vm);
                vm.AboutRequested = ShowAboutAsync;
                vm.Update.ChangelogRequested = ShowUpdateChangelogAsync;
                // No apply/reveal callbacks since SE-245: the updater replaces the app and restarts it
                // itself, so there is no relaunch for the host to carry out and no file to hand over.
                // Removing the leftover pre-Velopack install runs someone's uninstaller, so it asks first;
                // with no hook wired the command does nothing rather than proceeding unasked.
                vm.Update.ConfirmRemoveLegacyInstall = async () =>
                {
                    var dialog = new ConfirmDialog(
                        vm.Loc["UpdateLegacyInstallTitle"],
                        vm.Loc["UpdateLegacyInstallMessage"],
                        vm.Loc["UpdateLegacyInstallRemove"],
                        vm.Loc["Cancel"]);
                    return await dialog.ShowDialog<bool>(this);
                };
                RebuildKeyBindings();

                // A language switch fires Loc.PropertyChanged(null) — the correct "everything on
                // this object changed" signal, and Loc[key] does return the fresh string right away,
                // but that alone does not repaint anything already on screen (confirmed: neither more
                // dispatcher pumps nor an explicit InvalidateVisual on every control in the tree makes
                // a difference — the bindings themselves never re-pull the new value, this isn't a
                // paint/layout problem). Toggling DataContext off and back forces every binding under
                // it to tear down and re-create from scratch, which does re-read the fresh value —
                // the same "reuse a control, swap its DataContext" mechanism DocumentView already
                // relies on for tab reuse, just applied here to force a refresh instead.
                vm.Loc.PropertyChanged += (_, _) =>
                {
                    var dataContext = DataContext;
                    DataContext = null;
                    DataContext = dataContext;
                };
            }
        };
    }

    /// <summary>Whether the platform draws the caption buttons itself — true on macOS, where the traffic
    /// lights stay. Settable so the screenshot harness can render the Windows/Linux bar from a Mac, which is
    /// the only way to look at both bars without two machines (SE-291).</summary>
    public static bool SystemCaptionButtons { get; set; } = OperatingSystem.IsMacOS();

    // --- Window chrome (SE-291) --------------------------------------------------------------------
    // Dragging is the platform's, through the title bar's ElementRole. These are ours, because handing
    // them to the platform (the CloseButton/MinimizeButton roles) is what makes one window behave three
    // different ways.

    private void OnMinimiseClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximiseClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // With the client area extended, the window manager never sees a click on the title bar, so the
    // maximise-on-double-click it would normally do has to happen here. On macOS the decorations are still
    // Full and the platform does do it — hence the check rather than an unconditional toggle, which would
    // undo the platform's own and read as a double-click that does nothing.
    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        // The caption buttons live inside the title bar: two quick clicks on Minimise would otherwise
        // minimise and then maximise the window on the way out.
        if (e.Source is Visual source
            && (ReferenceEquals(source, CaptionButtons) || source.GetVisualAncestors().Contains(CaptionButtons)))
        {
            return;
        }

        var atTap = WindowState;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (TitleBarDoubleTap.Resolve(atTap, WindowState) is { } next)
                {
                    WindowState = next;
                }
            },
            DispatcherPriority.Background);
    }

    private void SyncMaximiseButton()
    {
        var maximised = WindowState == WindowState.Maximized;
        var loc = (DataContext as MainViewModel)?.Loc;

        // Two overlapping rounded squares for "restore", one for "maximise".
        MaximiseGlyph.Data = Geometry.Parse(maximised
            ? "M0.5,3.5 H7.5 V10.5 H0.5 Z M3.5,3.5 V0.5 H10.5 V7.5 H7.5"
            : "M0.5,0.5 H10.5 V10.5 H0.5 Z");

        ToolTip.SetTip(MaximiseButton, maximised
            ? loc?["WindowRestore"] ?? "Restore"
            : loc?["WindowMaximise"] ?? "Maximise");
    }

    // Materialize the live keymap into Window.KeyBindings. Called once the VM is attached and again on
    // every keymap change. Only Window-scoped commands land here; editor-scoped ones (toggle comment)
    // are handled by the SQL editor itself. Unparseable or unbound gestures are simply skipped.
    private void RebuildKeyBindings()
    {
        if (_keymap is null || DataContext is not MainViewModel vm)
        {
            return;
        }

        KeyBindings.Clear();
        foreach (var command in _keymap.Commands)
        {
            if (command.Scope != ShortcutScope.Window)
            {
                continue;
            }

            var gesture = _keymap.Resolve(command.Id);
            if (string.IsNullOrWhiteSpace(gesture)
                || vm.ResolveShortcut(command.Id) is not { } target
                || TryParseGesture(gesture) is not { } parsed)
            {
                continue;
            }

            KeyBindings.Add(new KeyBinding { Gesture = parsed, Command = target });
        }

        // Plugin-contributed shortcuts (always window-scoped): wrap each plugin action in a command.
        foreach (var plugin in _keymap.PluginShortcuts)
        {
            var gesture = _keymap.Resolve(plugin.Id);
            if (string.IsNullOrWhiteSpace(gesture) || TryParseGesture(gesture) is not { } parsed)
            {
                continue;
            }

            var action = plugin.ExecuteAsync;
            KeyBindings.Add(new KeyBinding
            {
                Gesture = parsed,
                Command = new AsyncRelayCommand(() => action(CancellationToken.None))
            });
        }
    }

    // KeyGesture.Parse throws on a malformed string; treat any bad persisted gesture as "no binding".
    private static KeyGesture? TryParseGesture(string gesture)
    {
        try
        {
            return KeyGesture.Parse(gesture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool _subsystemMenuBuilt;

    // SE-164 menu seam: append the plugin-contributed Tools-menu items after the static ones. Built once —
    // the DataContextChanged handler also fires on the language-switch DataContext toggle, which would
    // otherwise duplicate them. Plugin titles come from the plugin's localizer at startup.
    private void PopulateSubsystemMenu(MainViewModel vm)
    {
        if (_subsystemMenuBuilt || vm.SubsystemMenuItems.Count == 0)
        {
            return;
        }

        _subsystemMenuBuilt = true;
        ToolsMenu.Items.Add(new Separator());
        foreach (var node in vm.SubsystemMenuItems)
        {
            ToolsMenu.Items.Add(new MenuItem { Header = node.Title, Command = node.Run });
        }
    }

    private async Task ShowAboutAsync(ViewModels.AboutViewModel viewModel) =>
        await new AboutWindow(viewModel).ShowDialog(this);

    private async Task ShowUpdateChangelogAsync(ViewModels.UpdateAvailableViewModel viewModel) =>
        await new UpdateAvailableWindow(viewModel).ShowDialog(this);

    // The guided hand-off (SE-151): reveal the containing folder rather than launch the binary — the user
    // runs the installer themselves. Best-effort: a missing shell handler must not take the window down.
    private static Task RevealFolderAsync(string filePath)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(filePath) ?? filePath;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }

        return Task.CompletedTask;
    }

    private void RestoreLayout()
    {
        if (_settingsStore is null)
        {
            return;
        }

        var settings = _settingsStore.Load();

        // Guard against a zero/negative size: an interrupted first run (crash or a kill before the
        // window was ever measured) can persist 0x0, which would restore to an invisible window.
        if (settings.WindowWidth is { } w && settings.WindowHeight is { } h && w > 0 && h > 0)
        {
            Width = w;
            Height = h;
        }

        // Only honour a stored position when both coordinates are present, so a partially
        // written file can't drop the window at an off-screen corner.
        if (settings.WindowX is { } x && settings.WindowY is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)x, (int)y);
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        Body.RestoreSidebarWidth(settings.SidebarWidth);
    }

    // A restored position (RestoreLayout) can land off every monitor after a display change/unplug, which
    // would show the window somewhere invisible. Once opened (Screens is reliable then), if the window's
    // top-left is on no screen, recentre it on the primary/first screen's working area.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (Screens is not { All.Count: > 0 } screens || screens.All.Any(s => s.Bounds.Contains(Position)))
        {
            return;
        }

        var target = screens.Primary ?? screens.All[0];
        var area = target.WorkingArea;
        var width = (int)Math.Min(area.Width, FrameSize?.Width ?? area.Width);
        var height = (int)Math.Min(area.Height, FrameSize?.Height ?? area.Height);
        Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - width) / 2),
            area.Y + Math.Max(0, (area.Height - height) / 2));
    }

    private bool _forceClose;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        var settings = _settingsStore?.Load();

        // Close-to-tray: a user-initiated window close (the X button / Alt+F4) hides the window instead of
        // quitting, so the app — and the MCP server — keep running in the background. A real quit (File >
        // Exit, the tray's Quit item, or an OS shutdown) arrives with CloseReason != WindowClosing and falls
        // through to the normal close path below.
        if (!_forceClose
            && e.CloseReason == WindowCloseReason.WindowClosing
            && settings is { CloseToTray: true })
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Ask for confirmation first, unless the user turned it off (or already confirmed this close). Only
        // when the window is visible — a quit chosen from the tray while hidden has no visible owner for the
        // modal dialog, and is itself an explicit quit, so it closes without a second prompt.
        if (!_forceClose && IsVisible && settings is { ConfirmOnExit: true })
        {
            e.Cancel = true;
            _ = ConfirmExitAsync(_settingsStore!);
            return;
        }

        // Offer to save unsaved query files before the real close (SE-154). The prompt is async, so cancel
        // this close pass and re-close once it resolves (or stay open if the user cancels).
        if (!_dirtyHandled && DataContext is MainViewModel dirtyVm && dirtyVm.ShouldPromptSaveOnExit)
        {
            e.Cancel = true;
            _ = SaveDirtyThenCloseAsync(dirtyVm);
            return;
        }

        PersistLayout();
        (DataContext as MainViewModel)?.PersistOpenTabs();
        base.OnClosing(e);
    }

    private bool _dirtyHandled;

    private async Task SaveDirtyThenCloseAsync(MainViewModel vm)
    {
        if (!await vm.ConfirmCloseAllDirtyAsync())
        {
            _forceClose = false; // user cancelled — return to the normal (close-to-tray/confirm) behaviour
            return;
        }

        _dirtyHandled = true;
        _forceClose = true;
        Close();
    }

    private async Task ConfirmExitAsync(IAppSettingsStore store)
    {
        var loc = (DataContext as MainViewModel)?.Loc;
        var dialog = new ExitConfirmDialog(
            loc?["ExitConfirmTitle"] ?? "Quit",
            loc?["ExitConfirmMessage"] ?? "Are you sure you want to quit?",
            loc?["ExitConfirmQuit"] ?? "Quit",
            loc?["Cancel"] ?? "Cancel",
            loc?["ExitConfirmAlways"] ?? "Always quit without asking");

        var confirmed = await dialog.ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        // "Always" ticked → stop asking from now on (persist immediately, before the real close).
        if (dialog.Always)
        {
            var settings = store.Load();
            settings.ConfirmOnExit = false;
            try
            {
                store.Save(settings);
            }
            catch (Exception)
            {
                // Never block quitting on a failed preference write.
            }
        }

        _forceClose = true;
        Close();
    }

    private void PersistLayout()
    {
        if (_settingsStore is null)
        {
            return;
        }

        var settings = _settingsStore.Load();
        var maximized = WindowState == WindowState.Maximized;
        settings.WindowMaximized = maximized;

        // When maximized, Width/Height/Position describe the maximized frame; keep the last
        // normal-state values so restoring un-maximizes to a sane size and place. Also skip a
        // NaN/zero size — that means the window was never laid out (e.g. closed/killed during
        // startup), and persisting it would restore to an invisible 0x0 window next run.
        // (A relational pattern is already false for NaN, so `is > 0` covers the never-laid-out case.)
        if (!maximized && Width is > 0 && Height is > 0)
        {
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.WindowX = Position.X;
            settings.WindowY = Position.Y;
        }

        settings.SidebarWidth = Body.SidebarWidth;

        // Tool-window sizes (SE-123): read the live grid sizes back into the VM, then persist them
        // alongside the sidebar so a resize survives a restart.
        Body.CaptureToolWindowSizes();
        if (Body.DataContext is ViewModels.MainViewModel vm)
        {
            settings.OutputHeight = vm.OutputWindow.Size;
            settings.HistoryWidth = vm.HistoryWindow.Size;
        }

        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception)
        {
            // Never block window close on a failed preference write.
        }
    }
}
