using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using DataTray.App.DependencyInjection;
using DataTray.App.Startup;
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

    // The macOS application-menu items ("About DataTray", "Preferences…"), created here and filled in
    // once OnFrameworkInitializationCompleted has services — see the SE-278 comment on Initialize().
    private NativeMenuItem? _appMenuAboutItem;
    private NativeMenuItem? _appMenuSettingsItem;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // What macOS puts next to the Apple logo, and in "About …", "Hide …" and "Quit …". Avalonia
        // builds that menu itself and reads Application.Name for it — not the bundle's CFBundleName,
        // which is why a correct Info.plist still left it saying "Avalonia Application" (the property's
        // default when unset). No other platform shows this string.
        Name = "DataTray";

        // The application menu itself (the "About DataTray" item) has to be attached here too, not in
        // OnFrameworkInitializationCompleted where SE-254 originally put it (PR #180). Avalonia.Native's
        // menu exporter for the Apple-logo menu resets its layout in its own constructor, which runs
        // during platform init — before OnFrameworkInitializationCompleted's DI/subsystem setup even
        // starts. If NativeMenu.GetMenu(Application.Current) is still null then, it commits to its own
        // hardcoded "About Avalonia" fallback and never looks again (unlike the Dock-menu target, this
        // one has no subscription for a later change) — so SE-254's fix compiled and shipped but never
        // actually replaced the fallback on screen (SE-278). Setting an (empty) item now, before that
        // exporter constructs, is what makes it pick up ours on its first look. The item's Header and
        // Click are filled in later, once ILocalizer/MainViewModel exist — NativeMenuItem.Header changes
        // propagate live to the native menu (the native proxy subscribes to HeaderProperty), and Click
        // is a plain event, so binding it late is safe.
        //
        // Preferences moved in here too (SE-278, macOS convention: app menu, Cmd+,) out of the in-window
        // Edit menu, which held nothing else. Avalonia.Native appends Services/Hide/Quit after whatever
        // is already in this NativeMenu (AvaloniaNativeMenuExporter.PopulateStandardOSXMenuItems), so
        // About/Preferences land above them, matching the usual macOS app-menu order.
        _appMenuAboutItem = new NativeMenuItem();
        _appMenuSettingsItem = new NativeMenuItem { Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta) };
        NativeMenu.SetMenu(this, new NativeMenu
        {
            _appMenuAboutItem,
            new NativeMenuItemSeparator(),
            _appMenuSettingsItem
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ScreenshotMode)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                StartBehindSplash(desktop);
                break;
            case ISingleViewApplicationLifetime singleView:
                // No splash outside the desktop lifetime: build synchronously, as before SE-297.
                var services = AppServices.Build();
                var settings = services.GetRequiredService<IAppSettingsStore>().Load();
                ApplyAppearance(settings, services.GetRequiredService<ILocalizer>());
                ApplyQueryLog(services, settings);
                var viewModel = services.GetRequiredService<MainViewModel>();
                MountSubsystems(services, viewModel, services.GetRequiredService<KeymapService>());
                singleView.MainView = new MainView { DataContext = viewModel };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }

    // SE-297: the splash shows first, the plugin work runs on a worker so the splash can draw and count, and
    // everything that builds UI (view model, extension panels, the main window) stays on the UI thread.
    private void StartBehindSplash(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Theme and language before the first frame, so the splash is never drawn in the wrong theme.
        var settings = new Infrastructure.Persistence.JsonAppSettingsStore().Load();
        var localizer = new Localization.ResxLocalizer();
        ApplyAppearance(settings, localizer);

        var relaunched = Environment.GetCommandLineArgs().Contains(AppRestart.RelaunchArgument);
        var splashModel = new SplashViewModel(localizer, TimeProvider.System);
        var splash = new SplashWindow(splashModel, compact: relaunched, ReducedMotion.IsRequested());
        desktop.MainWindow = splash;

        Window front = splash;
        var handedOver = false;

        // Single-instance listener, started now rather than with the main window: a second launch during a
        // slow start surfaces the splash instead of finding nobody listening.
        SingleInstance.StartServer(
            () => Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowWindow(front)),
            _shutdownCts.Token);

        // Closing the splash before the hand-over is quitting; the main window must not appear afterwards.
        splash.Closed += (_, _) =>
        {
            if (!handedOver)
            {
                desktop.Shutdown();
            }
        };

        splashModel.RetryRequested = AppRestart.Restart;
        splashModel.QuitRequested = () => desktop.Shutdown();
        splashModel.OpenLogRequested = OpenLog;

        // "Continue without store plugins" starts a new run while a slow one may still be going; the old run
        // is cancelled so it can neither report over the new one nor open a second main window.
        CancellationTokenSource? currentRun = null;

        async Task RunAsync(bool bundledPluginsOnly)
        {
            currentRun?.Cancel();
            var run = currentRun = new CancellationTokenSource();
            var mainWindow = await StartupAsync(desktop, splashModel, localizer, bundledPluginsOnly, run.Token);
            if (mainWindow is null || run.IsCancellationRequested || !splash.IsVisible)
            {
                return;
            }

            try
            {
                // A short beat on "All set", then the main window takes over. No beat under reduced motion.
                await Task.Delay(splash.Classes.Contains("reduced-motion") ? 0 : 350);
                handedOver = true;
                front = mainWindow;
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
                splash.Close();
            }
            catch (Exception ex)
            {
                // Fire-and-forget from here on: a throw must land on the splash, not in an unobserved task.
                handedOver = false;
                front = splash;
                desktop.MainWindow = splash;
                RestartDiagnostics.Log($"start: hand-over FAILED {ex.GetType().Name}: {ex.Message}");
                splashModel.Fail(ex);
            }
        }

        splashModel.ContinueWithoutRequested = () => _ = RunAsync(bundledPluginsOnly: true);
        splash.Opened += (_, _) => _ = RunAsync(bundledPluginsOnly: false);
    }

    // Runs the steps and builds the main window. Null when a step failed: the splash then shows why.
    private async Task<MainWindow?> StartupAsync(IClassicDesktopStyleApplicationLifetime desktop,
        SplashViewModel splash, Localization.ResxLocalizer localizer, bool bundledPluginsOnly, CancellationToken ct)
    {
        IServiceProvider? services = null;
        MainViewModel? viewModel = null;
        KeymapService? keymap = null;
        Core.Plugins.SubsystemActivationResult? subsystems = null;
        var simulate = StartupSimulation.Current;

        var work = new List<Startup.StartupWork>();
        if (!bundledPluginsOnly && Core.Plugins.PluginMaintenance.HasPending(
                new Infrastructure.Persistence.JsonPluginStateStore(), Core.Plugins.PluginPaths.UserRoot))
        {
            work.Add(new(Startup.StartupSteps.Updates, _ => Task.Run(() =>
                Core.Plugins.PluginMaintenance.ApplyPending(
                    new Infrastructure.Persistence.JsonPluginStateStore(), Core.Plugins.PluginPaths.UserRoot))));
        }

        work.Add(new(Startup.StartupSteps.Plugins, async progress =>
            services = await Task.Run(() =>
            {
                simulate.During(Startup.StartupSteps.Plugins, bundledPluginsOnly);
                return AppServices.Build(localizer, progress, bundledPluginsOnly);
            })));
        work.Add(new(Startup.StartupSteps.Workspace, async _ =>
        {
            await LetTheSplashDrawAsync();
            simulate.During(Startup.StartupSteps.Workspace, bundledPluginsOnly);
            var built = services ?? throw new InvalidOperationException("The plugins step built no services.");
            ApplyQueryLog(built, built.GetRequiredService<IAppSettingsStore>().Load());
            viewModel = built.GetRequiredService<MainViewModel>();
            keymap = built.GetRequiredService<KeymapService>();
        }));
        work.Add(new(Startup.StartupSteps.Extensions, async _ =>
        {
            await LetTheSplashDrawAsync();
            if (services is null || viewModel is null || keymap is null)
            {
                throw new InvalidOperationException("The workspace step built no main view model.");
            }

            subsystems = MountSubsystems(services, viewModel, keymap);
        }));

        if (!await Startup.StartupRunner.RunAsync(splash, work, RestartDiagnostics.Log, ct)
            || services is null || viewModel is null || keymap is null || subsystems is null)
        {
            return null;
        }

        try
        {
            return WireDesktop(desktop, services, viewModel, keymap, subsystems);
        }
        catch (Exception ex)
        {
            RestartDiagnostics.Log($"start: main window FAILED {ex.GetType().Name}: {ex.Message}");
            splash.Fail(ex);
            return null;
        }
    }

    // The UI-thread steps block the dispatcher while they run; yield once at background priority first, so the
    // splash renders the step it is about to block on.
    private static Task LetTheSplashDrawAsync() =>
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(static () => { }, Avalonia.Threading.DispatcherPriority.Background)
            .GetTask();

    private static void OpenLog()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RestartDiagnostics.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RestartDiagnostics.Log($"start: open log FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ApplyAppearance(Core.Settings.AppSettings settings, ILocalizer localizer)
    {
        // Apply the saved theme/language before anything renders, so there's no flash of the
        // wrong theme or a Language toggle needed after every launch.
        ThemeApplier.Apply(settings.Theme);
        ThemeApplier.ApplyScrollbars(settings.OverlayScrollbars);
        if (settings.Language is { Length: > 0 } language)
        {
            localizer.SetCulture(CultureInfo.GetCultureInfo(language));
        }
    }

    private static void ApplyQueryLog(IServiceProvider services, Core.Settings.AppSettings settings) =>
        // Apply the saved query-log policy before any query can run (SettingsViewModel re-applies on save).
        services.GetRequiredService<Core.Logging.IQueryLog>()
            .Configure(settings.QueryLogEnabled, settings.QueryLogApp, settings.QueryLogMcp);

    private Core.Plugins.SubsystemActivationResult MountSubsystems(
        IServiceProvider services, MainViewModel viewModel, KeymapService keymap)
    {
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

        return subsystems;
    }

    private MainWindow WireDesktop(IClassicDesktopStyleApplicationLifetime desktop, IServiceProvider services,
        MainViewModel viewModel, KeymapService keymap, Core.Plugins.SubsystemActivationResult subsystems)
    {
        var settingsStore = services.GetRequiredService<IAppSettingsStore>();
        var mainWindow = new MainWindow(settingsStore, keymap) { DataContext = viewModel };

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

        // Fill in the macOS application-menu items created in Initialize() (SE-278) now that
        // services exist. Services/Hide/Quit stay with the platform. No-op off macOS: only that
        // backend exports an application menu, so the items are null elsewhere.
        var appMenuLoc = services.GetRequiredService<ILocalizer>();
        if (_appMenuAboutItem is not null)
        {
            _appMenuAboutItem.Header = $"{appMenuLoc["About"]} DataTray";
            _appMenuAboutItem.Click += (_, _) => viewModel.ShowAboutCommand.Execute(null);
        }
        if (_appMenuSettingsItem is not null)
        {
            _appMenuSettingsItem.Header = appMenuLoc["Preferences"];
            _appMenuSettingsItem.Click += (_, _) => viewModel.OpenSettingsCommand.Execute(null);
        }

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

        return mainWindow;
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
        // SE-292: the Security step's choice has to take effect in this process — the very next step writes
        // the first connection password, and a store swapped only on the next start would miss it.
        viewModel.UseFileStoreRequested = () =>
            services.GetRequiredService<Infrastructure.Secrets.SecretStoreSwitch>().Use(
                Infrastructure.Secrets.SecretStores.CreateFile(
                    services.GetRequiredService<Core.Security.IMasterKeyProvider>()));

        var window = new FirstRunWindow(viewModel);
        // Owned by the wizard, not the main window: a dialog parented behind an open modal is a window the
        // user can see and not reach.
        viewModel.StoreRequested = async pluginId =>
        {
            var store = services.GetRequiredService<ViewModels.PluginStoreViewModel>();
            store.PreselectPluginId = pluginId;
            // The store's own "Restart now" goes through the wizard's: a restart started here takes the
            // wizard down with it, so onboarding has to save its position first or it completes and never
            // comes back (SE-268).
            store.RestartRequested = () => viewModel.RestartNowCommand.Execute(null);
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
