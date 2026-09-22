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

public class BrowseRunTests
{
    [Fact] // SE-293: Run/F5 on a Data tab used to run the query editor's Sql — always "" on a browse tab,
           // which Mongo and Elastic reject with "Empty query." and which left the grid showing stale rows.
    public async Task Run_on_a_browse_tab_reloads_the_page_instead_of_the_empty_editor_text()
    {
        var document = NewDocument(out var connections, out var provider);
        var connection = connections.List().First();

        document.InitBrowse(connection, database: null, schema: null, "orders");
        await document.RunCommand.ExecuteAsync(null);

        Assert.Equal([$"SELECT * FROM \"orders\" LIMIT {document.PageSize} OFFSET 0"], provider.Executed);
    }

    private static DocumentViewModel NewDocument(out ConnectionService connections, out FakeProvider provider)
    {
        provider = new FakeProvider();
        var providers = new DbProviderRegistry([new ProviderRegistration("fake", provider)]);
        connections = new ConnectionService(new FakeConnectionStore(), new FakeSecretStore(), providers);
        connections.Save("c1", "Prod", "fake", new Dictionary<string, string?> { ["database"] = "master" });

        return new DocumentViewModel(providers, connections, new FakeFormatter(), new FakeHistory(), new FakeQueryLog(),
            new FakeSchemaCache(), new ServerVersionCache(), new FakeSettingsStore(), new FakeLocalizer());
    }

    // Records every statement that reaches the driver, whichever path sent it — the bug and the fix differ
    // only in what gets executed, not in whether anything does.
    private sealed class FakeProvider : IDbProvider
    {
        public List<string> Executed { get; } = [];

        public string DisplayName => "Fake DB";
        public ProviderIcon? Icon => null;
        public ISqlDialect Dialect { get; } = new FakeDialect();
        public bool IsSqlBased => true;
        public IReadOnlyList<ConnectionField> ConnectionFields => [new ConnectionField("database", "Database", ConnectionFieldType.Text)];
        public string BuildConnectionString(IReadOnlyDictionary<string, string?> values) => "fake";
        public Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct) => throw new NotSupportedException();

        public Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct)
        {
            Executed.Add(sql);
            return Task.FromResult(Empty());
        }

        public Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct)
        {
            Executed.Add(sql);
            return Task.FromResult<IReadOnlyList<QueryResult>>([Empty()]);
        }

        public Task<int> ExecuteBatchAsync(ConnectionProfile profile, IReadOnlyList<SqlStatement> statements, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<CreateCapability> CreateCapabilities => [];
        public IReadOnlyList<string> ColumnTypes => [];
        public SqlStatement BuildCreateStatement(CreateObjectSpec spec) => throw new NotSupportedException();
        public Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();

        private static QueryResult Empty() => new() { Columns = [new ResultColumn("id", typeof(int))], Rows = [] };
    }

    private sealed class FakeDialect : ISqlDialect
    {
        public IReadOnlySet<string> Keywords => new HashSet<string>();
        public string QuoteIdentifier(string identifier) => $"\"{identifier}\"";
        public string QualifyName(string? database, string? schema, string table) => QuoteIdentifier(table);
        public string Paginate(string sql, int limit, int offset, string? orderBy = null) =>
            $"{sql}{(orderBy is null ? string.Empty : $" ORDER BY {orderBy}")} LIMIT {limit} OFFSET {offset}";
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
