using System.ComponentModel;
using System.Globalization;
using DataTray.App.ViewModels;
using DataTray.Core.Connections;
using DataTray.Core.Localization;
using DataTray.Core.Providers;
using DataTray.Sdk;
using DataTray.Sdk.Branding;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Ddl;
using DataTray.Sdk.Query;
using DataTray.Sdk.Schema;

namespace DataTray.App.Tests;

public class ConnectionDialogViewModelTests
{
    [Fact] // SE-174: editing a connection then a provider re-select (the ComboBox's transient null->same-value
           // round-trip when the detail view re-attaches) must NOT reset the fields to the provider defaults.
    public void Editing_keeps_field_values_across_a_provider_reselect()
    {
        var providers = new DbProviderRegistry([new ProviderRegistration("fake", new FakeFieldsProvider())]);
        var connections = new ConnectionService(new FakeConnectionStore(), new FakeSecretStore(), providers);
        var saved = connections.Save("c1", "Prod", "fake", new Dictionary<string, string?>
        {
            ["host"] = "db.internal", ["port"] = "5555", ["password"] = "secret",
        });

        var vm = new ConnectionDialogViewModel(connections, providers, new FakeLocalizer());
        vm.LoadForEdit(saved);
        Assert.Equal("db.internal", FieldValue(vm, "host"));

        // Reproduce the spurious re-fire: SelectedProvider goes null then back to the same provider.
        var provider = vm.SelectedProvider;
        vm.SelectedProvider = null;
        vm.SelectedProvider = provider;

        Assert.Equal("db.internal", FieldValue(vm, "host"));   // was reset to the "localhost" default before the fix
        Assert.Equal("5555", FieldValue(vm, "port"));

        var reSaved = vm.Save();
        Assert.Equal("db.internal", reSaved.Values["host"]);   // and the reset defaults are not persisted
    }

    [Fact] // SE-287: the providers already declare ConnectionField.Group; the old form threw it away and
           // rendered one flat list. The sections are that metadata, not a new model.
    public void Advanced_fields_are_cut_into_the_groups_the_provider_declares()
    {
        var vm = NewViewModel();

        // "SSH tunnel" is the host's own block (SE-18), which rides along for host-based providers and
        // is grouped by exactly the same mechanism.
        Assert.Equal(["Security", "Connection", "SSH tunnel"], vm.AdvancedGroups.Select(g => g.Title));
        Assert.Equal(["encrypt"], vm.AdvancedGroups[0].Fields.Select(f => f.Field.Key));
        Assert.All(vm.AdvancedGroups, g => Assert.True(g.HasTitle));

        // The provider's basics carry no group, so they stay one untitled block at the top of the section.
        var basics = Assert.Single(vm.BasicGroups);
        Assert.Null(basics.Title);
        Assert.False(basics.HasTitle);
        Assert.Equal(["host", "port", "password"], basics.Fields.Select(f => f.Field.Key));
    }

    [Fact] // SE-287: same seven palette values as the old Ellipse row, now selectable by name — including
           // "no colour", which used to be an empty circle that said nothing.
    public void Colour_options_round_trip_through_the_named_dropdown()
    {
        var vm = NewViewModel();

        Assert.Equal(7, vm.ColorOptions.Count);
        Assert.Null(vm.ColorOptions[0].Value);
        Assert.False(vm.ColorOptions[0].HasColor);
        Assert.Equal(["ColorNone", "ColorRed"], vm.ColorOptions.Take(2).Select(o => o.Name)); // via the localiser

        vm.SelectedColorOption = vm.ColorOptions[1];
        Assert.Equal("#E5484D", vm.Color);
        Assert.Same(vm.ColorOptions[1], vm.SelectedColorOption); // and back out again
        Assert.True(vm.HasColor);

        vm.Color = null;
        Assert.Same(vm.ColorOptions[0], vm.SelectedColorOption);
        Assert.False(vm.HasColor);
    }

    [Fact] // SE-287: the radio buttons replace a ComboBox. An unchecked one must NOT write back — that
           // would race the newly checked level straight back to None and silently revoke access.
    public void Ai_access_radio_buttons_only_act_on_the_checked_side()
    {
        var vm = NewViewModel();
        Assert.True(vm.IsAiAccessNone); // fail-closed default is unchanged

        vm.IsAiAccessReadWrite = true;
        Assert.Equal(AiAccessMode.ReadWrite, vm.AiAccess);
        Assert.False(vm.IsAiAccessNone);
        Assert.True(vm.ShowAiWriteWarning);
        Assert.True(vm.ShowAiPill);

        vm.IsAiAccessReadWrite = false; // the "I was unchecked" notification
        Assert.Equal(AiAccessMode.ReadWrite, vm.AiAccess);

        vm.ExcludeFromMcp = true; // hard block wins over the granted level
        Assert.False(vm.ShowAiPill);
    }

    private static ConnectionDialogViewModel NewViewModel()
    {
        var providers = new DbProviderRegistry([new ProviderRegistration("fake", new FakeFieldsProvider())]);
        var connections = new ConnectionService(new FakeConnectionStore(), new FakeSecretStore(), providers);
        return new ConnectionDialogViewModel(connections, providers, new FakeLocalizer());
    }

    private static string? FieldValue(ConnectionDialogViewModel vm, string key) =>
        vm.Fields.First(f => f.Field.Key == key).Value;

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

    // A minimal SQL provider with host/port/password fields — the create/edit paths only read ConnectionFields.
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
            // Two advanced fields in two declared groups: what SE-287's sub-sections are built from.
            new ConnectionField("encrypt", "Encrypt", ConnectionFieldType.Bool, Group: "Security", Advanced: true),
            new ConnectionField("timeout", "Timeout", ConnectionFieldType.Number, Group: "Connection", Advanced: true),
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
