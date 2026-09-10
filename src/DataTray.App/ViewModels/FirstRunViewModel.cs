using System.Collections.ObjectModel;
using DataTray.Core.Connections;
using DataTray.Core.Connections.Import;
using DataTray.Core.Localization;
using DataTray.Core.Plugins;
using DataTray.Core.Providers;
using DataTray.Core.Settings;
using DataTray.Core.Store;
using DataTray.Infrastructure.Secrets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DataTray.App.ViewModels;

/// <summary>Where the first-run wizard is. Persisted as an int in <see cref="AppSettings.OnboardingStep"/>,
/// so the order of these members is a storage format — append, never reorder.</summary>
public enum FirstRunStep
{
    Welcome,
    Engine,
    Connection,
    Done
}

/// <summary>One engine tile on step 2. An engine is either <see cref="IsInstalled"/> — loadable now, so the
/// wizard can go straight on to its fields — or available from the Plugin Store, which is a download and a
/// restart away.</summary>
public sealed partial class FirstRunEngine(string id, string displayName, bool isInstalled) : ObservableObject
{
    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    public bool IsInstalled { get; } = isInstalled;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Backs the first-run wizard (SE-239): Welcome → Engine → Connection → Done, shown once on a fresh
/// profile and never again.
/// </summary>
/// <remarks>
/// The wizard owns no connection logic of its own. Step 3 hosts a real
/// <see cref="ConnectionDialogViewModel"/> (its <see cref="ConnectionDialogViewModel.BasicFields"/> only) or
/// a real <see cref="ImportConnectionsDialogViewModel"/>, so anything the connection dialog learns to do,
/// onboarding gets for free.
///
/// Installing an engine is the one place the wizard cannot be a thin shell. Plugins load at startup — an
/// install is staged and applies on the next run (<see cref="PluginCatalogService.HasPendingChanges"/>) — so
/// a provider chosen here is not loadable until the app restarts. Rather than dead-end the user, the wizard
/// writes its position to <see cref="AppSettings"/>, restarts, and resumes on the same step with the engine
/// now available.
/// </remarks>
public partial class FirstRunViewModel : ViewModelBase
{
    private readonly IAppSettingsStore _settingsStore;
    private readonly ConnectionService _connections;
    private readonly IDbProviderRegistry _providers;
    private readonly PluginCatalogService _plugins;
    private readonly IStoreCatalog _storeCatalog;
    private readonly Func<ConnectionDialogViewModel> _newConnectionDialog;

    // Set while the app is restarting to load a just-installed engine: the window closes, but onboarding is
    // not finished and its saved position must survive.
    private bool _restarting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsEngine), nameof(IsConnection), nameof(IsDone),
        nameof(CanGoBack), nameof(CanGoNext), nameof(ShowSkip), nameof(ShowNext), nameof(IsImporting),
        nameof(IsManualConnection), nameof(WelcomeDone), nameof(EngineDone), nameof(ConnectionDone))]
    private FirstRunStep _step;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext), nameof(ConnectionIntro))]
    private FirstRunEngine? _selectedEngine;

    /// <summary>Step 3 shows the import list instead of the connection form.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImporting), nameof(IsManualConnection), nameof(CanGoNext))]
    private bool _importChosen;

    /// <summary>Set after the Plugin Store staged an install: the chosen engine only loads after a restart,
    /// so the wizard offers one rather than walking into a step it cannot render.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private bool _restartNeeded;

    [ObservableProperty]
    private string _connectionsAdded = string.Empty;

    /// <summary>Non-empty once this run resumed after a restart that loaded a freshly installed engine —
    /// step 2 confirms the install by name instead of leaving it implied by a selected tile.</summary>
    [ObservableProperty]
    private string _installedNotice = string.Empty;

    public FirstRunViewModel(
        IAppSettingsStore settingsStore,
        ConnectionService connections,
        IDbProviderRegistry providers,
        PluginCatalogService plugins,
        IStoreCatalog storeCatalog,
        ILocalizer localizer,
        Func<ConnectionDialogViewModel> newConnectionDialog)
    {
        _settingsStore = settingsStore;
        _connections = connections;
        _providers = providers;
        _plugins = plugins;
        _storeCatalog = storeCatalog;
        _newConnectionDialog = newConnectionDialog;
        Loc = localizer;
        Import = new ImportConnectionsDialogViewModel(localizer)
        {
            // SE-238: onboarding gets the same opt-in password fetch the Connection Manager's picker has.
            FetchPasswordsRequested = found => ExternalConnectionImport.WithStoredPasswords(
                found, ForeignSecretLookups.ForThisPlatform(), FieldKeysOf)
        };

        LoadInstalledEngines();

        // Resume where a restart-for-a-plugin left off, with the engine that was chosen — which is the whole
        // reason the position was written down. An unknown/uninstalled id falls back to the step's default.
        var settings = settingsStore.Load();
        // Never resume onto Done: that step only reports what the previous run saved, which this run has no
        // record of, so it would open on "You're set" with nothing to say.
        if (settings.OnboardingStep is > 0 and < (int)FirstRunStep.Done)
        {
            SelectedEngine = Engines.FirstOrDefault(e => e.Id == settings.OnboardingProviderId && e.IsInstalled);
            if (SelectedEngine is { } resumed)
            {
                resumed.IsSelected = true;
                // A position only survives a restart-for-a-plugin, so getting here means an install landed.
                // Say which engine outright — a highlighted tile is not confirmation.
                InstalledNotice = Loc.Get("FirstRunInstalled", resumed.DisplayName);
            }

            Step = SelectedEngine is null ? FirstRunStep.Engine : (FirstRunStep)settings.OnboardingStep;
            if (Step == FirstRunStep.Connection)
            {
                StartConnectionStep();
            }
        }
    }

    public ILocalizer Loc { get; }

    /// <summary>Opens the Plugin Store window on the given plugin id (null for no particular one). The store
    /// owns install: its capability-consent gate and host-API check are not something onboarding may skip
    /// past.</summary>
    public Func<string?, Task>? StoreRequested { get; set; }

    /// <summary>Restarts the app (the same <c>AppRestart</c> the Plugin Store uses).</summary>
    public Action? RestartRequested { get; set; }

    /// <summary>Closes the wizard window.</summary>
    public Action? CloseRequested { get; set; }

    public ObservableCollection<FirstRunEngine> Engines { get; } = [];

    /// <summary>Provider plugins in the store that aren't installed — named, not installable inline.</summary>
    public ObservableCollection<FirstRunEngine> StoreEngines { get; } = [];

    public bool HasStoreEngines => StoreEngines.Count > 0;

    /// <summary>Step 3's import face. Empty until <see cref="Configure"/> feeds it a scan.</summary>
    public ImportConnectionsDialogViewModel Import { get; }

    /// <summary>Step 3's manual face — a real connection dialog, rendered as its basic fields only.</summary>
    [ObservableProperty]
    private ConnectionDialogViewModel? _connection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiscovered), nameof(DiscoveredLine))]
    private int _discoveredCount;

    public bool HasDiscovered => DiscoveredCount > 0;

    public string DiscoveredLine => Loc.Get("FirstRunImportFound", DiscoveredCount);

    public bool IsWelcome => Step == FirstRunStep.Welcome;

    public bool IsEngine => Step == FirstRunStep.Engine;

    public bool IsConnection => Step == FirstRunStep.Connection;

    public bool IsDone => Step == FirstRunStep.Done;

    // Stepper ticks: a step reads as done once the wizard is past it.
    public bool WelcomeDone => Step > FirstRunStep.Welcome;

    public bool EngineDone => Step > FirstRunStep.Engine;

    public bool ConnectionDone => Step > FirstRunStep.Connection;

    public bool IsImporting => IsConnection && ImportChosen;

    public bool IsManualConnection => IsConnection && !ImportChosen;

    public bool CanGoBack => Step is FirstRunStep.Engine or FirstRunStep.Connection;

    /// <summary>Skip is offered on every step but the last, where there is nothing left to skip.</summary>
    public bool ShowSkip => Step != FirstRunStep.Done;

    /// <summary>Welcome has its own brand-blue "Get started" and Done has "Open DataTray"; the middle steps
    /// get the accent Next.</summary>
    public bool ShowNext => Step is FirstRunStep.Engine or FirstRunStep.Connection;

    public bool CanGoNext => Step switch
    {
        FirstRunStep.Welcome => true,
        FirstRunStep.Engine => SelectedEngine is { IsInstalled: true } && !RestartNeeded,
        FirstRunStep.Connection => ImportChosen
            ? Import.Selected.Count > 0
            : Connection is { CanSave: true },
        _ => false
    };

    public string ConnectionIntro =>
        Loc.Get("FirstRunConnectionIntro", SelectedEngine?.DisplayName ?? string.Empty);

    /// <summary>Scan the machine for other clients' connections. Off the UI thread — it walks a handful of
    /// config files under the home directory and must not stall the window opening.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        Configure(await Task.Run(() => ExternalConnectionImport.Discover(FieldKeysOf), ct));
        await LoadStoreEnginesAsync(ct);
    }

    /// <summary>Fill the import step from a scan. Split out from <see cref="InitializeAsync"/> so the rows
    /// can be supplied without touching the machine's real config files.</summary>
    public void Configure(IReadOnlyList<DiscoveredConnection> found)
    {
        Import.Configure(found);
        DiscoveredCount = found.Count(c => c.CanImport);
    }

    [RelayCommand]
    private void Next()
    {
        switch (Step)
        {
            case FirstRunStep.Welcome:
                Step = FirstRunStep.Engine;
                break;
            case FirstRunStep.Engine:
                StartConnectionStep();
                break;
            case FirstRunStep.Connection:
                Commit();
                Step = FirstRunStep.Done;
                break;
        }

        Remember();
    }

    [RelayCommand]
    private void Back()
    {
        Step = Step == FirstRunStep.Connection ? FirstRunStep.Engine : FirstRunStep.Welcome;
        ImportChosen = false;
        Remember();
    }

    /// <summary>Leave onboarding without finishing it. Marks it done: someone who skipped has said no, and
    /// asking again on the next launch would be the app arguing with them.</summary>
    [RelayCommand]
    private void Skip()
    {
        Finish();
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void SelectEngine(FirstRunEngine? engine)
    {
        if (engine is null)
        {
            return;
        }

        SelectedEngine = engine;
        foreach (var candidate in Engines)
        {
            candidate.IsSelected = ReferenceEquals(candidate, engine);
        }

        Remember();
    }

    /// <summary>Hand off to the Plugin Store to install an engine, then check what it staged. The engine whose
    /// tile was clicked is passed on, so the store opens on that plugin rather than on whatever its own list
    /// happened to select first.</summary>
    [RelayCommand]
    private async Task OpenStore(FirstRunEngine? engine)
    {
        if (StoreRequested is null)
        {
            return;
        }

        // Select it before handing off: the restart that a staged install needs is what writes the position
        // down, and without this the wizard would come back with no engine chosen.
        if (engine is not null)
        {
            SelectEngine(engine);
        }

        await StoreRequested(engine?.Id);

        // Anything staged only takes effect on the next start, so the wizard cannot continue into step 3
        // with that engine — it offers the restart it needs and resumes afterwards.
        RestartNeeded = _plugins.HasPendingChanges;
        LoadInstalledEngines();
    }

    [RelayCommand]
    private void RestartNow()
    {
        _restarting = true;
        Remember();
        RestartRequested?.Invoke();
    }

    /// <summary>Take the import route on step 3 instead of typing a connection by hand.</summary>
    [RelayCommand]
    private void StartImport()
    {
        ImportChosen = true;
        Step = FirstRunStep.Connection;
        Remember();
    }

    /// <summary>
    /// Mark onboarding done. Called by Skip, by the last step's button, and by the window closing — dismissing
    /// the wizard is an answer, and asking again next launch would be the app arguing with the user.
    /// </summary>
    /// <remarks>
    /// Except across a restart-for-a-plugin. That closes the window too, and completing there would throw
    /// away the position <see cref="RestartNow"/> just saved: the user would come back to no wizard at all,
    /// having installed an engine for a connection they never got to make.
    /// </remarks>
    [RelayCommand]
    private void Finish()
    {
        if (_restarting)
        {
            return;
        }

        var settings = _settingsStore.Load();
        settings.OnboardingCompleted = true;
        settings.OnboardingStep = 0;
        settings.OnboardingProviderId = null;
        _settingsStore.Save(settings);
    }

    [RelayCommand]
    private void Close()
    {
        Finish();
        CloseRequested?.Invoke();
    }

    private void StartConnectionStep()
    {
        if (SelectedEngine is not { } engine)
        {
            return;
        }

        var dialog = _newConnectionDialog();
        dialog.SelectedProvider = dialog.AvailableProviders.FirstOrDefault(p => p.Id == engine.Id)
                                  ?? dialog.SelectedProvider;
        // CanSave gates Next, and it only raises PropertyChanged on the dialog — forward it.
        dialog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ConnectionDialogViewModel.CanSave))
            {
                OnPropertyChanged(nameof(CanGoNext));
            }
        };

        Connection = dialog;
        ImportChosen = false;
        Step = FirstRunStep.Connection;
    }

    /// <summary>Persist whatever step 3 produced, and say how much of it landed.</summary>
    private void Commit()
    {
        var added = 0;
        if (ImportChosen)
        {
            added = ImportedConnections.SaveAll(_connections, Import.Selected).Count;
        }
        else if (Connection is { CanSave: true } dialog)
        {
            dialog.Save();
            added = 1;
        }

        // One is the common case on a first run, and "1 connections ready" reads like a bug.
        ConnectionsAdded = added switch
        {
            0 => Loc["FirstRunDoneNone"],
            1 => Loc["FirstRunDoneCountOne"],
            _ => Loc.Get("FirstRunDoneCount", added)
        };
    }

    /// <summary>Write down where we are, so a restart for a freshly installed plugin comes back here.</summary>
    private void Remember()
    {
        var settings = _settingsStore.Load();
        settings.OnboardingStep = (int)Step;
        settings.OnboardingProviderId = SelectedEngine?.Id;
        _settingsStore.Save(settings);
    }

    private void LoadInstalledEngines()
    {
        Engines.Clear();
        foreach (var registration in _providers.All.OrderBy(r => r.Provider.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            Engines.Add(new FirstRunEngine(registration.Id, registration.Provider.DisplayName, isInstalled: true));
        }
    }

    // Provider plugins the store offers that aren't loaded here. Best-effort: a fresh profile is often
    // offline or behind a proxy, and a store that can't be reached must leave the wizard usable rather than
    // fail it — the four bundled engines are enough to finish onboarding.
    private async Task LoadStoreEnginesAsync(CancellationToken ct)
    {
        StoreCatalog catalog;
        try
        {
            catalog = await _storeCatalog.FetchAsync(ct);
        }
        catch (Exception)
        {
            return;
        }

        var installed = Engines.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Entries
                     .Select(e => e.Entry)
                     .Where(e => string.Equals(e.Type, "provider", StringComparison.OrdinalIgnoreCase))
                     .Where(e => !installed.Contains(e.Id))
                     .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            StoreEngines.Add(new FirstRunEngine(entry.Id, entry.Name, isInstalled: false));
        }

        OnPropertyChanged(nameof(HasStoreEngines));
    }

    // Same answer the Connection Manager uses: a provider whose plugin isn't installed has no field keys, so
    // its discovered connections are listed but not importable.
    private IReadOnlyList<string>? FieldKeysOf(string providerId) =>
        _providers.TryGet(providerId, out var provider)
            ? provider.ConnectionFields.Select(f => f.Key).ToList()
            : null;
}
