using DataTray.Sdk;
using DataTray.Sdk.Branding;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Ddl;
using DataTray.Sdk.Query;
using DataTray.Sdk.Schema;

namespace DataTray.App.Tests;

/// <summary>A provider the tree fixture only asks for its identity and dialect.</summary>
internal sealed class TreeStubProvider : IDbProvider
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
