using System.ComponentModel;
using System.Globalization;
using DataTray.App.ViewModels;
using DataTray.Core.Connections;
using DataTray.Core.Connections.Import;
using DataTray.Core.Localization;
using DataTray.Core.Plugins;
using DataTray.Core.Providers;
using DataTray.Core.Settings;
using DataTray.Core.Store;
using DataTray.Sdk;
using DataTray.Sdk.Branding;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Ddl;
using DataTray.Sdk.Query;
using DataTray.Sdk.Schema;

namespace DataTray.App.Tests;

public class FirstRunViewModelTests
{
    [Fact] // Skipping is an answer, not a postponement: asking again next launch is the app arguing back.
    public void Skipping_completes_onboarding_and_forgets_the_position()
    {
        var settings = new FakeSettingsStore();
        var vm = Build(settings);

        vm.NextCommand.Execute(null);                       // Welcome -> Engine
        Assert.Equal(FirstRunStep.Engine, vm.Step);
        Assert.Equal((int)FirstRunStep.Engine, settings.Current.OnboardingStep);

        vm.SkipCommand.Execute(null);

        Assert.True(settings.Current.OnboardingCompleted);
        Assert.Equal(0, settings.Current.OnboardingStep);
        Assert.Null(settings.Current.OnboardingProviderId);
    }

    [Fact] // The whole reason the position is written down: installing an engine forces a restart, and the
           // wizard has to come back to the step and engine the user had chosen.
    public void A_restart_resumes_on_the_step_and_engine_it_left()
    {
        var settings = new FakeSettingsStore
        {
            Current =
            {
                OnboardingStep = (int)FirstRunStep.Connection,
                OnboardingProviderId = "fake"
            }
        };

        var vm = Build(settings);

        Assert.Equal(FirstRunStep.Connection, vm.Step);
        Assert.Equal("fake", vm.SelectedEngine?.Id);
        Assert.True(vm.SelectedEngine?.IsSelected);
        Assert.NotNull(vm.Connection);                       // and the form is standing, not still to be built
        Assert.Equal("fake", vm.Connection!.SelectedProvider?.Id);

        // SE-268: the install is confirmed by name, not left to be inferred from a highlighted tile.
        Assert.NotEmpty(vm.InstalledNotice);
    }

    [Fact] // SE-268: every store tile shared one parameterless command, so the store never learned which
           // engine was clicked and opened on whatever its own list sorted first.
    public async Task Installing_a_store_engine_opens_the_store_on_that_engine()
    {
        var vm = Build(new FakeSettingsStore());
        var storeEngine = new FirstRunEngine("clickhouse", "ClickHouse", isInstalled: false);
        string? openedOn = "not called";
        vm.StoreRequested = id => { openedOn = id; return Task.CompletedTask; };

        await vm.OpenStoreCommand.ExecuteAsync(storeEngine);

        Assert.Equal("clickhouse", openedOn);
    }

    [Fact] // SE-268: the store's own "Restart now" is routed through the wizard, which must write down the
           // engine being installed — otherwise the resume has nothing to select.
    public async Task A_restart_from_the_store_remembers_the_engine_being_installed()
    {
        var settings = new FakeSettingsStore();
        var vm = Build(settings);
        vm.RestartRequested = () => { };
        var storeEngine = new FirstRunEngine("clickhouse", "ClickHouse", isInstalled: false);

        vm.NextCommand.Execute(null);                        // -> Engine
        // The store restarts from inside the hand-off, the way App wires it.
        vm.StoreRequested = _ =>
        {
            vm.RestartNowCommand.Execute(null);
            return Task.CompletedTask;
        };

        await vm.OpenStoreCommand.ExecuteAsync(storeEngine);

        vm.FinishCommand.Execute(null);                      // the window closing on the way down

        Assert.False(settings.Current.OnboardingCompleted);
        Assert.Equal((int)FirstRunStep.Engine, settings.Current.OnboardingStep);
        Assert.Equal("clickhouse", settings.Current.OnboardingProviderId);
    }

    [Fact] // A remembered provider whose plugin is gone (uninstalled between runs) must not resume into a
           // step that cannot render its fields.
    public void A_resume_onto_a_missing_provider_falls_back_to_the_engine_step()
    {
        var settings = new FakeSettingsStore
        {
            Current =
            {
                OnboardingStep = (int)FirstRunStep.Connection,
                OnboardingProviderId = "uninstalled-since"
            }
        };

        var vm = Build(settings);

        Assert.Equal(FirstRunStep.Engine, vm.Step);
        Assert.Null(vm.SelectedEngine);
        Assert.Null(vm.Connection);
    }

    [Fact]
    public void Finishing_the_manual_route_saves_the_connection_and_completes_onboarding()
    {
        var settings = new FakeSettingsStore();
        var connections = NewConnectionService();
        var vm = Build(settings, connections);

        vm.NextCommand.Execute(null);                        // -> Engine
        vm.SelectEngineCommand.Execute(vm.Engines.Single());
        Assert.True(vm.CanGoNext);
        vm.NextCommand.Execute(null);                        // -> Connection

        vm.Connection!.Name = "Production EU";
        Field(vm.Connection, "host").Value = "db1.internal";
        vm.NextCommand.Execute(null);                        // -> Done

        Assert.Equal(FirstRunStep.Done, vm.Step);
        var saved = Assert.Single(connections.List());
        Assert.Equal("Production EU", saved.Name);
        Assert.Equal("db1.internal", saved.Values["host"]);

        // Done is not complete: the wizard is still open and Skip is gone, so closing it is what commits.
        vm.CloseCommand.Execute(null);
        Assert.True(settings.Current.OnboardingCompleted);
    }

    [Fact] // Restarting to load a just-installed engine closes the window, which is also what "onboarding is
           // over" looks like. Completing there would strand the user: an engine installed, no wizard left,
           // and no connection made.
    public void Restarting_for_a_plugin_keeps_the_position_instead_of_completing()
    {
        var settings = new FakeSettingsStore();
        var vm = Build(settings);
        var restarted = false;
        vm.RestartRequested = () => restarted = true;

        vm.NextCommand.Execute(null);                        // -> Engine
        vm.SelectEngineCommand.Execute(vm.Engines.Single());
        vm.RestartNowCommand.Execute(null);

        Assert.True(restarted);

        // The window closing on the way out must not complete onboarding.
        vm.FinishCommand.Execute(null);

        Assert.False(settings.Current.OnboardingCompleted);
        Assert.Equal((int)FirstRunStep.Engine, settings.Current.OnboardingStep);
        Assert.Equal("fake", settings.Current.OnboardingProviderId);
    }

    [Fact] // The import route saves every ticked row and leaves the unticked ones alone.
    public void The_import_route_saves_only_the_ticked_rows()
    {
        var settings = new FakeSettingsStore();
        var connections = NewConnectionService();
        var vm = Build(settings, connections);

        vm.Configure([
            Discovered("prod-eu-1", "db1.internal"),
            Discovered("staging", "db-staging"),
            // A row whose provider isn't installed: listed, but not importable and never saved.
            new DiscoveredConnection("Compass", "atlas-cluster", null, ProviderId: null,
                new Dictionary<string, string?>(), SkipReason: "mongodb plugin not installed")
        ]);

        Assert.Equal(2, vm.DiscoveredCount);

        vm.StartImportCommand.Execute(null);
        Assert.Equal(FirstRunStep.Connection, vm.Step);
        Assert.True(vm.IsImporting);

        vm.Import.Rows.Single(r => r.Name == "staging").IsSelected = false;
        vm.NextCommand.Execute(null);

        var saved = Assert.Single(connections.List());
        Assert.Equal("prod-eu-1", saved.Name);
    }

    [Fact] // Two clients exporting the same connection name must not overwrite each other.
    public void Importing_a_name_that_is_taken_gets_a_suffix()
    {
        var connections = NewConnectionService();
        connections.Save("existing", "prod-eu-1", "fake", new Dictionary<string, string?> { ["host"] = "old" });

        ImportedConnections.SaveAll(connections, [Discovered("prod-eu-1", "db1.internal")]);

        Assert.Equal(
            ["prod-eu-1", "prod-eu-1 (2)"],
            connections.List().Select(c => c.Name).Order().ToArray());
    }

    private static DiscoveredConnection Discovered(string name, string host) =>
        new("DBeaver", name, null, "fake", new Dictionary<string, string?> { ["host"] = host });

    private static ConnectionFieldInput Field(ConnectionDialogViewModel vm, string key) =>
        vm.Fields.First(f => f.Field.Key == key);

    private static ConnectionService NewConnectionService() =>
        new(new FakeConnectionStore(), new FakeSecretStore(), Providers);

    private static readonly DbProviderRegistry Providers =
        new([new ProviderRegistration("fake", new FakeFieldsProvider())]);

    private static FirstRunViewModel Build(FakeSettingsStore settings, ConnectionService? connections = null)
    {
        connections ??= NewConnectionService();
        var localizer = new FakeLocalizer();
        return new FirstRunViewModel(
            settings,
            connections,
            Providers,
            new PluginCatalogService(new FakePluginStateStore(), [], []),
            new FakeStoreCatalog(),
            localizer,
            () => new ConnectionDialogViewModel(connections, Providers, localizer));
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public AppSettings Current { get; set; } = new();
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }

    private sealed class FakeStoreCatalog : IStoreCatalog
    {
        public Task<StoreCatalog> FetchAsync(CancellationToken ct) =>
            Task.FromResult(new StoreCatalog([], [], []));
    }

    private sealed class FakePluginStateStore : IPluginStateStore
    {
        private readonly Dictionary<string, PluginStateEntry> _entries = [];
        public IReadOnlyDictionary<string, PluginStateEntry> GetAll() => _entries;
        public PluginStateEntry Get(string id) => _entries.TryGetValue(id, out var e) ? e : new PluginStateEntry();
        public void Save(string id, PluginStateEntry entry) => _entries[id] = entry;
        public void Remove(string id) => _entries.Remove(id);
    }

    private sealed class FakeLocalizer : ILocalizer
    {
        public CultureInfo Culture => CultureInfo.InvariantCulture;
        public string this[string key] => key;
        public string Get(string key, params object[] args) => key;
        public void SetCulture(CultureInfo culture) { }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private sealed class FakeConnectionStore : IConnectionStore
    {
        private readonly List<SavedConnection> _items = [];
        public IReadOnlyList<SavedConnection> GetAll() => _items.ToList();
        public IReadOnlyDictionary<string, int> GetFolderOrder() => new Dictionary<string, int>();
        public void Save(SavedConnection c) { _items.RemoveAll(x => x.Id == c.Id); _items.Add(c); }
        public void Delete(string id) => _items.RemoveAll(x => x.Id == id);
        public void SaveAll(IReadOnlyList<SavedConnection> connections, IReadOnlyDictionary<string, int> folderOrder)
        { _items.Clear(); _items.AddRange(connections); }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = [];
        public void Set(string key, string secret) => _secrets[key] = secret;
        public string? Get(string key) => _secrets.TryGetValue(key, out var v) ? v : null;
        public void Delete(string key) => _secrets.Remove(key);
    }

    private sealed class FakeFieldsProvider : IDbProvider
    {
        public string DisplayName => "Fake DB";
        public ProviderIcon? Icon => null;
        public ISqlDialect Dialect => throw new NotSupportedException();
        public bool IsSqlBased => true;

        public IReadOnlyList<ConnectionField> ConnectionFields =>
        [
            new ConnectionField("host", "Host", ConnectionFieldType.Text, Required: true, Default: "localhost"),
            new ConnectionField("port", "Port", ConnectionFieldType.Number, Default: "1234"),
            new ConnectionField("password", "Password", ConnectionFieldType.Password),
        ];

        public string BuildConnectionString(IReadOnlyDictionary<string, string?> values) => "fake";
        public Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ExecuteBatchAsync(ConnectionProfile profile, IReadOnlyList<SqlStatement> statements, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<CreateCapability> CreateCapabilities => [];
        public IReadOnlyList<string> ColumnTypes => [];
        public SqlStatement BuildCreateStatement(CreateObjectSpec spec) => throw new NotSupportedException();
        public Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
    }
}
