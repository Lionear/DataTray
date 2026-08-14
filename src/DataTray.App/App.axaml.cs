using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using DataTray.App.DependencyInjection;
using DataTray.App.Theming;
using DataTray.App.ViewModels;
using DataTray.App.Views;
using DataTray.Core.Localization;
using DataTray.Core.Settings;
using DataTray.Core.Shortcuts;
using Microsoft.Extensions.DependencyInjection;

namespace DataTray.App;

public partial class App : Application
{
    // Held for the lifetime of the app so it is not garbage-collected while the app runs.
    private TrayIcon? _trayIcon;
    private readonly CancellationTokenSource _shutdownCts = new();

    // Screenshot capture (DataTray.Screenshots) reuses this App for its XAML styles/theme, but drives
    // the window itself under a headless lifetime. It must NOT wire the desktop shell (tray, MCP server,
    // updater, master-password gate) — set before framework init so OnFrameworkInitializationCompleted
    // short-circuits after the base call.
    public static bool ScreenshotMode { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // What macOS puts next to the Apple logo, and in "About …", "Hide …" and "Quit …". Avalonia
        // builds that menu itself and reads Application.Name for it — not the bundle's CFBundleName,
        // which is why a correct Info.plist still left it saying "Avalonia Application" (the property's
        // default when unset). No other platform shows this string.
        Name = "DataTray";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ScreenshotMode)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var services = AppServices.Build();

        // Apply the saved theme/language before anything renders, so there's no flash of the
        // wrong theme or a Language toggle needed after every launch.
        var settingsStore = services.GetRequiredService<IAppSettingsStore>();
        var settings = settingsStore.Load();
        ThemeApplier.Apply(settings.Theme);
        if (settings.Language is { Length: > 0 } language)
        {
            services.GetRequiredService<ILocalizer>().SetCulture(CultureInfo.GetCultureInfo(language));
        }

        // Apply the saved query-log policy before any query can run (SettingsViewModel re-applies on save).
        services.GetRequiredService<Core.Logging.IQueryLog>()
            .Configure(settings.QueryLogEnabled, settings.QueryLogApp, settings.QueryLogMcp);

        var viewModel = services.GetRequiredService<MainViewModel>();
        var keymap = services.GetRequiredService<KeymapService>();

        // Activate standing-subsystem plugins (SE-164) now the container is built: only here do the services
        // their capability-gated contexts need — plugin storage and, crucially, the ConnectionService behind
        // managed connections — actually exist. Held for Deactivate at shutdown (wired below in the desktop case).
        var subsystems = services.GetRequiredService<Core.Plugins.SubsystemActivator>().ActivateAll();

        // Host UI handed to plugin panels + menu actions (SE-164): ShowDialogAsync routes to the main window's
        // modal host via the VM delegate the view wires up.
        var hostUi = new DependencyInjection.SubsystemHostUi(viewModel.ShowPluginDialogAsync, viewModel.ConfirmPluginAsync);

        // Mount any panel contributions as bottom tool-windows. Done before the window's DataContext is set
        // (below), so MainView subscribes these panels' windows along with Output/History in one pass.
        foreach (var panel in subsystems.Panels)
        {
            viewModel.AddSubsystemPanel(panel.PanelId, panel.Title, panel.CreatePanel(hostUi), panel.Icon);
        }

        // First-party "AI activity" panel (SE-159): a bottom tool-window fed by the MCP audit ring, mounted
        // the same way as the plugin panels so it gets a status-bar toggle for free. Renders its own chrome.
        var loc0 = services.GetRequiredService<ILocalizer>();
        var aiActivity = new Views.AiActivityView
        {
            DataContext = new ViewModels.AiActivityViewModel(
                services.GetRequiredService<Core.Mcp.McpActivityLog>(),
                services.GetRequiredService<Core.Connections.ConnectionService>(),
                loc0)
        };
        var aiActivityPanel = viewModel.AddSubsystemPanel("AiActivity", loc0["AiActivity"], aiActivity, ViewModels.NodeIcons.AiActivity);

        // Only offer the AI-activity toggle while the MCP server is actually running (SE-183) — an empty panel
        // for a stopped server is just clutter. React live to start/stop; hide the panel if it was open.
        var mcpService = services.GetRequiredService<Mcp.Hosting.McpService>();
        void SyncAiActivityAvailability()
        {
            aiActivityPanel.IsAvailable = mcpService.IsRunning;
            if (!mcpService.IsRunning)
            {
                aiActivityPanel.IsVisible = false;
            }
        }

        SyncAiActivityAvailability();
        mcpService.StateChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(SyncAiActivityAvailability);

        // Mount any Tools-menu contributions (SE-164 menu seam).
        foreach (var menuPlugin in subsystems.Menus)
        {
            foreach (var item in menuPlugin.MenuItems)
            {
                var invoke = item.InvokeAsync;
                viewModel.AddSubsystemMenuItem(item.Title, () => invoke(hostUi));
            }
        }

        // Mount any connection context-menu contributions (SE-164): the tree's context menu shows them for a
        // right-clicked connection the item applies to.
        foreach (var connMenu in subsystems.ConnectionMenus)
        {
            foreach (var item in connMenu.ConnectionMenuItems)
            {
                var invoke = item.InvokeAsync;
                viewModel.AddConnectionMenuItem(item.Title, item.AppliesTo, info => invoke(info, hostUi));
            }
        }

        // Mount any application-toolbar contributions (SE-255). They join the action catalog, so the strip
        // and Settings ▸ Toolbar see them in one pass — and, being absent from the saved layout, a freshly
        // installed plugin's button shows up rather than waiting to be ticked on.
        foreach (var contributor in subsystems.Toolbars)
        {
            foreach (var item in contributor.Plugin.ToolbarItems)
            {
                var invoke = item.InvokeAsync;
                var id = $"{contributor.PluginId}:{item.Id}";
                viewModel.AddToolbarAction(
                    id, item.Title, contributor.PluginName, item.Icon, item.Tooltip, () => invoke(hostUi));

                // Every toolbar action is bindable (SE-255 §3.4), which is what makes hiding one harmless.
                // DefaultGesture is only a suggestion: null ships it unbound but still rebindable.
                keymap.Register([new Core.Shortcuts.PluginShortcut(
                    id, contributor.PluginId, contributor.PluginName, item.Title, item.DefaultGesture,
                    _ => invoke(hostUi))]);
            }
        }

        // Query-window toolbar contributions (SE-255): handed to every document, which filters them with
        // their own AppliesTo predicate per tab.
        viewModel.MountQueryToolbarContributions(
            [.. subsystems.QueryToolbars.SelectMany(p => p.QueryToolbarItems)], hostUi);

        // Start any background loops (SE-164) under the shutdown token — fire-and-forget, like the update
        // checks; they stop cleanly when _shutdownCts cancels at exit (desktop.Exit, below).
        foreach (var background in subsystems.Background)
        {
            _ = background.RunAsync(_shutdownCts.Token);
        }

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                var mainWindow = new MainWindow(settingsStore, keymap) { DataContext = viewModel };
                desktop.MainWindow = mainWindow;

                // System-tray presence so the app can keep running in the background (notably the MCP
                // server) after a close-to-tray. The icon is always available while the app runs; whether
                // the window's close button hides or quits is governed by the CloseToTray setting, which
                // MainWindow reads live on each close. Quit from here or File > Exit routes through
                // desktop.Shutdown(), which closes with CloseReason=ApplicationShutdown (a real quit).
                // Opening the log from the tray surfaces the window first (it may be hidden), then runs the
                // same command the menu uses — which shows the non-modal, single-instance Query Log window.
                void OpenQueryLog()
                {
                    ShowWindow(mainWindow);
                    (mainWindow.DataContext as MainViewModel)?.OpenQueryLogCommand.Execute(null);
                }

                _trayIcon = BuildTrayIcon(desktop, mainWindow, services.GetRequiredService<ILocalizer>(), OpenQueryLog);

                // Single-instance listener: a second launch (e.g. clicking the app again while it's hidden
                // in the tray) signals us here instead of opening a new window. Marshal onto the UI thread.
                SingleInstance.StartServer(
                    () => Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowWindow(mainWindow)),
                    _shutdownCts.Token);

                desktop.Exit += (_, _) =>
                {
                    _shutdownCts.Cancel();
                    // Best-effort teardown of the standing-subsystem plugins (SE-164) — one failing Deactivate
                    // never blocks the rest (SubsystemRegistry swallows), and this must not hold up exit.
                    subsystems.Registry.DeactivateAll();
                    // Drop any MCP-created transient connections (SE-155): they are session-only, held only in
                    // memory, and must never outlive the process.
                    services.GetRequiredService<Core.Connections.ConnectionService>().ClearTransient();
                    // Close every SSH tunnel (SE-18) rather than leave a forwarded loopback port and a live
                    // bastion session behind for as long as the process lingers.
                    services.GetRequiredService<Core.Connections.Ssh.ISshTunnelManager>().CloseAll();
                    _trayIcon?.Dispose();
                };
                // Stop the MCP listener cleanly on exit so its loopback port is released promptly. Run it
                // OFF the UI thread with a timeout: awaiting StopAsync's continuations back onto the (blocked)
                // UI thread would deadlock and hang shutdown; the OS reclaims the port regardless, so this is
                // best-effort and must never block exit.
                desktop.ShutdownRequested += (_, _) =>
                {
                    try
                    {
                        Task.Run(() => services.GetRequiredService<Mcp.Hosting.McpService>().StopAsync())
                            .Wait(TimeSpan.FromSeconds(2));
                    }
                    catch
                    {
                        // Best-effort: never let a slow/failed stop hold the app open.
                    }
                };

                // Master password: gate the (already-shown) main window behind an unlock prompt on open, and
                // re-prompt when the idle timeout auto-locks. Connection secrets aren't decrypted until the
                // user actually connects, so the empty shell behind the modal reveals nothing.
                var masterPassword = services.GetRequiredService<Core.Security.MasterPasswordService>();
                var loc = services.GetRequiredService<ILocalizer>();
                // The first-run wizard (SE-239) rides the same handler rather than its own, so it can never
                // stack a second modal on top of the unlock prompt: unlock first, onboarding only after.
                mainWindow.Opened += async (_, _) =>
                {
                    if (masterPassword.IsEnabled)
                    {
                        await GateUnlockAsync(mainWindow, masterPassword, desktop, loc);
                        if (!masterPassword.IsUnlocked)
                        {
                            return;
                        }
                    }

                    if (!settingsStore.Load().OnboardingCompleted)
                    {
                        await ShowFirstRunAsync(mainWindow, services, _shutdownCts.Token);
                    }
                };
                services.GetRequiredService<Core.Security.IMasterKeyProvider>().Locked += () =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => _ = GateUnlockAsync(mainWindow, masterPassword, desktop, loc));

                // In-app updater (SE-137): check the chosen channel once the window is up, then periodically
                // while the app stays open (notably close-to-tray). Fully async and fault-tolerant — offline
                // or a fetch failure is silent; the banner appears via binding only for a newer, non-dismissed
                // build. Both stop cleanly on shutdown via the shared token.
                mainWindow.Opened += (_, _) =>
                {
                    _ = viewModel.Update.CheckOnStartupAsync(_shutdownCts.Token);
                    _ = viewModel.Update.RunPeriodicChecksAsync(_shutdownCts.Token);
                    // Proactive plugin-update check (SE-138), same fire-and-forget lifecycle as the app-updater.
                    // First confirm anything the Auto policy staged last run and this startup just applied.
                    viewModel.PluginUpdates.ReportRestartSummaryIfAny();
                    _ = viewModel.PluginUpdates.CheckOnStartupAsync(_shutdownCts.Token);
                    _ = viewModel.PluginUpdates.RunPeriodicChecksAsync(_shutdownCts.Token);
                };
                break;
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView { DataContext = viewModel };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static TrayIcon BuildTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, Window window, ILocalizer loc, Action openQueryLog)
    {
        var show = new NativeMenuItem(loc["TrayShow"]);
        show.Click += (_, _) => ShowWindow(window);

        var queryLog = new NativeMenuItem(loc["QueryLogMenu"]);
        queryLog.Click += (_, _) => openQueryLog();

        var quit = new NativeMenuItem(loc["Exit"]);
        quit.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Add(show);
        menu.Add(queryLog);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        var tray = new TrayIcon
        {
            Icon = LoadTrayIcon(),
            ToolTipText = "DataTray",
            IsVisible = true,
            Menu = menu
        };
        // Left-click the tray icon also restores the window (a common convention on Windows/Linux).
        tray.Clicked += (_, _) => ShowWindow(window);
        return tray;
    }

    // The first-run wizard (SE-239). Modal over the main window, so the empty shell behind it can't be
    // clicked into a confusing half-state. Installing an engine hands off to the Plugin Store window — its
    // capability-consent gate and host-API check are not something onboarding may route around.
    private static async Task ShowFirstRunAsync(Window owner, IServiceProvider services, CancellationToken ct)
    {
        var viewModel = services.GetRequiredService<ViewModels.FirstRunViewModel>();
        viewModel.RestartRequested = AppRestart.Restart;

        var window = new FirstRunWindow(viewModel);
        // Owned by the wizard, not the main window: a dialog parented behind an open modal is a window the
        // user can see and not reach.
        viewModel.StoreRequested = async () =>
        {
            var store = services.GetRequiredService<ViewModels.PluginStoreViewModel>();
            await new PluginStoreWindow { DataContext = store }.ShowDialog(window);
        };

        // Scanning for other clients' connections and fetching the store catalog both touch the world, so
        // they run once the window is up rather than delaying it.
        window.Opened += async (_, _) => await viewModel.InitializeAsync(ct);
        await window.ShowDialog(owner);
    }

    private bool _unlocking;

    // Loop the unlock dialog until the master key is provided (validated inline) or the user quits. Guarded
    // so the startup gate and an idle-lock re-prompt can't stack two dialogs.
    private async Task GateUnlockAsync(Window owner, Core.Security.MasterPasswordService service,
        IClassicDesktopStyleApplicationLifetime desktop, ILocalizer loc)
    {
        if (_unlocking || !service.IsEnabled || service.IsUnlocked)
        {
            return;
        }

        _unlocking = true;
        try
        {
            while (!service.IsUnlocked)
            {
                var dialog = new MasterPasswordDialog(MasterPasswordMode.Unlock, loc, service.TryUnlock);
                var result = await dialog.ShowDialog<MasterPasswordDialogResult?>(owner);
                if (result is null)
                {
                    desktop.Shutdown(); // user chose Quit rather than unlock
                    return;
                }
                // On success the inline validator already unlocked the provider; the loop condition exits.
            }
        }
        finally
        {
            _unlocking = false;
        }
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Activate();
    }

    private static WindowIcon LoadTrayIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://DataTray.App/Assets/icon.png"));
        return new WindowIcon(stream);
    }
}
