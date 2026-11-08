using System.ComponentModel;
using System.Globalization;
using DataTray.App.ViewModels;
using DataTray.Core.Connections;
using DataTray.Core.History;
using DataTray.Core.Localization;
using DataTray.Core.Logging;
using DataTray.Core.Providers;
using DataTray.Core.Schema;
using DataTray.Core.Settings;
using DataTray.Sdk;
using DataTray.Sdk.Branding;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Ddl;
using DataTray.Sdk.Extensibility;
using DataTray.Sdk.Formatting;
using DataTray.Sdk.Query;
using DataTray.Sdk.Schema;

namespace DataTray.App.Tests;

/// <summary>Builds a real <see cref="DocumentViewModel"/> over in-memory fakes, with a provider whose
/// database listings a test controls (SE-267).</summary>
internal static class QueryDocumentFixture
{
    public static Task<IReadOnlyList<string>> Ready(params string[] databases) =>
        Task.FromResult<IReadOnlyList<string>>(databases);

    public static DocumentViewModel NewDocument(out ConnectionService connections, params Task<IReadOnlyList<string>>[] listings)
    {
        var providers = new DbProviderRegistry([new ProviderRegistration("fake", new FakeProvider(listings))]);
        connections = new ConnectionService(new FakeConnectionStore(), new FakeSecretStore(), providers);
        connections.Save("c1", "Prod", "fake", new Dictionary<string, string?> { ["database"] = "master" });
        connections.Save("c2", "Staging", "fake", new Dictionary<string, string?> { ["database"] = "staging" });

        return new DocumentViewModel(providers, connections, new FakeFormatter(), new FakeHistory(), new FakeQueryLog(),
            new FakeSchemaCache(), new ServerVersionCache(), new FakeSettingsStore(), new FakeLocalizer());
    }

    // The refresh runs off the InitQuery call as fire-and-forget; poll rather than sleep a fixed amount.
    public static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the database list never arrived");
    }

    // Hands out one prepared database listing per GetDatabasesAsync call, so a test can let the first
    // one answer and leave the next hanging.
    private sealed class FakeProvider(Task<IReadOnlyList<string>>[] listings) : IDbProvider
    {
        private int _calls;


        public string DisplayName => "Fake DB";
        public ProviderIcon? Icon => null;
        public ISqlDialect Dialect => throw new NotSupportedException();
        public bool IsSqlBased => true;
        public IReadOnlyList<ConnectionField> ConnectionFields => [new ConnectionField("database", "Database", ConnectionFieldType.Text)];
        public string BuildConnectionString(IReadOnlyDictionary<string, string?> values) => "fake";
        public Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ExecuteBatchAsync(ConnectionProfile profile, IReadOnlyList<SqlStatement> statements, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<CreateCapability> CreateCapabilities => [];
        public IReadOnlyList<string> ColumnTypes => [];
        public SqlStatement BuildCreateStatement(CreateObjectSpec spec) => throw new NotSupportedException();
        public Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct) =>
            listings[Math.Min(_calls++, listings.Length - 1)];
        public Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
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

    private sealed class FakeFormatter : ISqlFormatter
    {
        public string Format(string sql, ISqlDialect dialect, SqlFormatOptions options) => sql;
    }

    private sealed class FakeHistory : IQueryHistoryStore
    {
        public event Action? Changed { add { } remove { } }
        public void Append(QueryHistoryEntry entry) { }
        public IReadOnlyList<QueryHistoryEntry> GetRecent(int limit) => [];
        public IReadOnlyList<QueryHistoryEntry> Search(string text) => [];
        public void Clear() { }
    }

    private sealed class FakeQueryLog : IQueryLog
    {
        public event Action? Changed { add { } remove { } }
        public void Configure(bool enabled, bool logApp, bool logMcp) { }
        public void Record(QueryHistoryEntry entry) { }
        public IReadOnlyList<QueryHistoryEntry> Read(QueryLogFilter filter) => [];
        public void Clear() { }
    }

    private sealed class FakeSchemaCache : ISchemaCache
    {
        public event Action? Changed { add { } remove { } }
        public SchemaSnapshot? Get(string connectionId) => null;
        public Task BuildAsync(SavedConnection connection, CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate(string connectionId) { }
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public AppSettings Load() => new();
        public void Save(AppSettings settings) { }
    }

    private sealed class FakeLocalizer : ILocalizer
    {
        public CultureInfo Culture => CultureInfo.InvariantCulture;
        public string this[string key] => key;
        public string Get(string key, params object[] args) => key;
        public void SetCulture(CultureInfo culture) { }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }
}
