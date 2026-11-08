using DataTray.App.ViewModels;
using DataTray.Core.Connections;
using DataTray.Sdk;
using DataTray.Sdk.Branding;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Ddl;
using DataTray.Sdk.Query;
using DataTray.Sdk.Schema;

namespace DataTray.App.Tests;

/// <summary>Builds a loaded tree Prod › Sales › dbo › {customers (table), active_customers (view)}.
/// Needs <see cref="HeadlessDrawing"/>: node icons parse geometry.</summary>
internal static class TreeNodeFixture
{
    public static TreeNodeViewModel Node(string name) => Schema().Children.First(c => c.Name == name);

    public static TreeNodeViewModel Schema()
    {
        var connection = new SavedConnection
        {
            Id = "c1", Name = "Prod", ProviderId = "fake", Values = new Dictionary<string, string?>(),
        };
        var root = TreeNodeViewModel.ForConnection(connection, new StubProvider(), iconImage: null, Load);
        root.IsExpanded = true;
        var database = root.Children.Single();
        database.IsExpanded = true;
        var schema = database.Children.Single();
        schema.IsExpanded = true;
        return schema;
    }

    private static Task<IReadOnlyList<DbTreeNode>> Load(SavedConnection connection, IReadOnlyList<DbNodeRef> path)
    {
        IReadOnlyList<DbTreeNode> children = path.Count switch
        {
            0 => [new DbTreeNode { Kind = DbNodeKind.Database, Name = "Sales", HasChildren = true }],
            1 => [new DbTreeNode { Kind = DbNodeKind.Schema, Name = "dbo", HasChildren = true }],
            _ => [new DbTreeNode { Kind = DbNodeKind.Table, Name = "customers" },
                  new DbTreeNode { Kind = DbNodeKind.View, Name = "active_customers" }],
        };

        return Task.FromResult(children);
    }

    /// <summary>Spells out the parts it was given, so a test sees exactly what reached QualifyName.</summary>
    public sealed class RecordingDialect : ISqlDialect
    {
        public IReadOnlySet<string> Keywords => new HashSet<string>();
        public string QuoteIdentifier(string identifier) => $"[{identifier}]";
        public string QualifyName(string? database, string? schema, string table) => $"{database}|{schema}|{table}";
        public string Paginate(string sql, int limit, int offset, string? orderBy = null) => sql;
    }

    private sealed class StubProvider : IDbProvider
    {
        public string DisplayName => "Fake DB";
        public ProviderIcon? Icon => null;
        public ISqlDialect Dialect { get; } = new RecordingDialect();
        public bool IsSqlBased => true;
        public IReadOnlyList<ConnectionField> ConnectionFields => [];
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
