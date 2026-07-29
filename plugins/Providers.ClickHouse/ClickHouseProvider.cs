using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClickHouse.Driver.ADO;
using DataTray.Sdk;
using DataTray.Sdk.Provisioning;
using DataTray.Sdk.Security;

namespace DataTray.Providers.ClickHouse;

/// <summary>
/// A ClickHouse provider (SE-36). ClickHouse is a genuine SQL engine, so unlike MongoDB/Elasticsearch this
/// keeps <see cref="IDbProvider.IsSqlBased"/> and reuses the host's SQL generation throughout. What it is
/// *not* is a row-oriented OLTP database, and four consequences of that shape the implementation:
/// <list type="bullet">
/// <item><b>One statement per request.</b> The HTTP interface rejects a multi-statement body outright
///   (<c>Code: 62 … Multi-statements are not allowed</c>), so <see cref="ExecuteScriptAsync"/> splits the
///   text itself (<see cref="ClickHouseScript"/>) and sends one request per statement instead of walking
///   a batch with <c>NextResult</c> the way the four bundled ADO.NET providers do.</item>
/// <item><b>No transactions.</b> ClickHouse has no <c>BEGIN</c>/<c>COMMIT</c> for ordinary MergeTree work,
///   so <see cref="ExecuteBatchAsync"/> cannot honour the SDK's "any failure rolls the whole batch back"
///   guarantee. It runs the statements in order and lets the failure surface, leaving the earlier ones
///   applied — see the note there.</item>
/// <item><b>Read-only result grids.</b> The editable-grid flow needs per-column <c>BaseTable</c> +
///   <c>IsKey</c> metadata to trace a result back to one table; the HTTP protocol carries no such
///   information, so those stay unset and the host keeps every grid read-only. That is the honest
///   outcome rather than a gap: ClickHouse edits rows through asynchronous
///   <c>ALTER TABLE … UPDATE/DELETE</c> mutations, which the host's generated
///   <c>UPDATE … WHERE key = …</c> would not express anyway.</item>
/// <item><b>No stored routines or triggers.</b> The tree therefore stops at Tables/Views with their
///   columns and data-skipping indexes; the routine capabilities stay off.</item>
/// </list>
/// It ships from the repo-root <c>plugins/</c> folder (not <c>src/</c>) and is staged only in Debug builds,
/// so it is directly usable while developing but installed from the Plugin Store in a release.
/// </summary>
public sealed class ClickHouseProvider : IDbProvider
{
    public string DisplayName => "ClickHouse";

    // Uses the embedded brand PNG (icon.png) when present; falls back to a glyph otherwise.
    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(ClickHouseProvider), "📊");

    public ISqlDialect Dialect { get; } = new ClickHouseDialect();

    // How to spin up an empty local ClickHouse container matching a connection (SE-166). The port is the
    // HTTP one (8123), not the native 9000, because the driver speaks binary-over-HTTP. The three
    // CLICKHOUSE_* env vars are the official image's init contract; DEFAULT_ACCESS_MANAGEMENT is what makes
    // the created user able to run CREATE USER/GRANT at all, without which this provider's user management
    // fails against its own container. Lazy `=> new(...)` keeps the ContainerRecipe type untouched until
    // the host reads it.
    public ContainerRecipe? ContainerRecipe => new(
        Image: "clickhouse",
        DefaultTag: "lts",
        ContainerPort: 8123,
        DataPath: "/var/lib/clickhouse",
        DefaultUser: "default",
        DefaultPassword: "changeme",
        Environment: e =>
        {
            var list = new List<KeyValuePair<string, string>>
            {
                new("CLICKHOUSE_USER", string.IsNullOrWhiteSpace(e.User) ? "default" : e.User),
                new("CLICKHOUSE_PASSWORD", e.Password),
                new("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1")
            };

            if (e.Database is { Length: > 0 })
            {
                list.Add(new("CLICKHOUSE_DB", e.Database));
            }

            return list;
        });

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("host", "Host", ConnectionFieldType.Text, Required: true, Default: "localhost"),
        new("port", "Port", ConnectionFieldType.Number, Default: "8123",
            Placeholder: "8123 (HTTP) / 8443 (HTTPS)"),
        new("database", "Database", ConnectionFieldType.Text, Required: true, Default: "default"),
        new("username", "Username", ConnectionFieldType.Text, Required: true, Default: "default"),
        new("password", "Password", ConnectionFieldType.Password),

        // Advanced. ClickHouse Cloud and any TLS-terminated deployment need https (and usually port 8443);
        // a plain local container is http. Compression is on by default in the driver and worth keeping —
        // exposed here because it is the one knob that matters over a slow link.
        new("protocol", "Protocol", ConnectionFieldType.Choice, Default: "http",
            Group: "Security", Advanced: true, Choices: ["http", "https"]),
        new("compression", "Compress responses", ConnectionFieldType.Bool, Default: "true",
            Group: "Performance", Advanced: true)
    ];

    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values)
    {
        var builder = new ClickHouseConnectionStringBuilder
        {
            Host = Value(values, "host") ?? "localhost",
            Database = Value(values, "database") ?? "default",
            Username = Value(values, "username") ?? "default",
            Password = Value(values, "password") ?? string.Empty
        };

        if (ushort.TryParse(Value(values, "port"), out var port))
        {
            builder.Port = port;
        }

        if (Value(values, "protocol") is { } protocol)
        {
            builder.Protocol = protocol;
        }

        if (Value(values, "compression") is { } compression && bool.TryParse(compression, out var compress))
        {
            builder.Compression = compress;
        }

        return builder.ConnectionString;
    }

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    // Inverse of BuildConnectionString. Like NpgsqlConnectionStringBuilder (and unlike SqlClient), every
    // keyword reports ContainsKey == true because they all carry defaults, so this guards on a non-empty
    // value instead — a partial paste must never blank the host or the credentials.
    public IReadOnlyDictionary<string, string?>? ParseConnectionString(string connectionString)
    {
        var b = new ClickHouseConnectionStringBuilder(connectionString);
        var result = new Dictionary<string, string?>();

        void Put(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                result[key] = value;
            }
        }

        Put("host", b.Host);
        Put("port", b.Port.ToString(CultureInfo.InvariantCulture));
        Put("database", b.Database);
        Put("username", b.Username);
        Put("password", b.Password);
        Put("protocol", b.Protocol);
        Put("compression", b.Compression ? "true" : "false");

        return result;
    }

    public async Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        return connection.State == ConnectionState.Open;
    }

    // ClickHouseConnection.ServerVersion deliberately throws ("no longer available — use
    // ExecuteScalarAsync(\"SELECT version()\")"), unlike every other ADO.NET driver the host uses, so this
    // costs one cheap round-trip. The host caches the result per connection, so it happens once.
    public async Task<string?> GetServerVersionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        try
        {
            await using var connection = await OpenAsync(profile, ct);
            await using var command = connection.CreateCommand("SELECT version()");
            return (await command.ExecuteScalarAsync(ct))?.ToString();
        }
        catch
        {
            // The version is decoration in the status bar; never fail a connect over it.
            return null;
        }
    }

    public async Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await ReadResultAsync(reader, stopwatch, ct);
    }

    /// <summary>
    /// Runs each statement in <paramref name="sql"/> as its own request — see <see cref="ClickHouseScript"/>
    /// for why a single multi-statement request is not an option. One connection is reused for the whole
    /// script so a <c>USE</c>-like database context and the connection cost are shared. A statement that
    /// produces no rows (DDL/DML) still yields its own empty <see cref="QueryResult"/>, so the tab count
    /// matches the statement count; a failure propagates immediately, leaving the earlier statements applied.
    /// </summary>
    public async Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var statements = ClickHouseScript.Split(sql);
        if (statements.Count == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(profile, ct);

        var results = new List<QueryResult>(statements.Count);
        foreach (var statement in statements)
        {
            ct.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand(statement);
            await using var reader = await command.ExecuteReaderAsync(ct);
            results.Add(await ReadResultAsync(reader, stopwatch, ct));
        }

        return results;
    }

    private static async Task<QueryResult> ReadResultAsync(DbDataReader reader, Stopwatch stopwatch, CancellationToken ct)
    {
        var columns = BuildColumns(reader);

        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row!);
            for (var i = 0; i < row.Length; i++)
            {
                if (row[i] is DBNull)
                {
                    row[i] = null;
                }
            }

            rows.Add(row);
        }

        return new QueryResult
        {
            Columns = columns,
            Rows = rows,
            RecordsAffected = reader.RecordsAffected,
            Elapsed = stopwatch.Elapsed
        };
    }

    // Name and CLR type only. ClickHouse's HTTP protocol carries no base-table/primary-key information for
    // a result set (contrast MySqlConnector, which fills BaseTable/IsKey from the wire protocol's column
    // definitions), so the edit metadata stays unset and the host keeps the grid read-only — see the class
    // remarks for why that is the right outcome and not a missing feature.
    private static List<ResultColumn> BuildColumns(DbDataReader reader)
    {
        var columns = new List<ResultColumn>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(new ResultColumn(reader.GetName(i), reader.GetFieldType(i)));
        }

        return columns;
    }

    /// <summary>
    /// Runs the statements in order. <b>This is not atomic</b>, and cannot be: ClickHouse has no
    /// transaction for ordinary MergeTree work, so there is nothing to roll back to. A failure halts the
    /// batch and propagates, leaving the statements before it applied. The host only reaches this path for
    /// an editable grid's save — which ClickHouse never enables (see the class remarks) — and for tool
    /// plugins that write through the SDK, so in practice it services INSERTs, where partial application is
    /// the same outcome a bare <c>clickhouse-client</c> script would produce.
    /// <para>The returned count is always 0 for INSERTs: ClickHouse's HTTP interface reports no affected-row
    /// count, so the driver's <c>ExecuteNonQueryAsync</c> has nothing to return. Verified against 26.3 —
    /// the rows do land. Reporting the real 0 beats inventing <c>statements.Count</c>.</para>
    /// </summary>
    public async Task<int> ExecuteBatchAsync(
        ConnectionProfile profile,
        IReadOnlyList<SqlStatement> statements,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);

        var affected = 0;
        foreach (var statement in statements)
        {
            ct.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand(string.Empty);
            Bind(command, statement);
            affected += await command.ExecuteNonQueryAsync(ct);
        }

        return affected;
    }

    /// <summary>
    /// Rewrites the host's <c>@name</c> placeholders into ClickHouse's <c>{name:Type}</c> form and binds
    /// the values. The host generates one shape of parameterised SQL for every SQL provider; ClickHouse is
    /// the only engine here that does not accept it, so the translation happens at the last possible
    /// moment. The concrete type in the braces is whatever the driver infers for the value
    /// (<c>ClickHouseDbParameter.QueryForm</c>) — a null has no inferable type (the driver would render
    /// <c>Nullable(Nothing)</c>, which the server rejects), so nulls become the literal <c>NULL</c> instead
    /// of a parameter.
    /// </summary>
    private static void Bind(ClickHouseCommand command, SqlStatement statement)
    {
        var text = statement.Text;

        foreach (var parameter in statement.Parameters)
        {
            var name = parameter.Name.TrimStart('@');

            if (parameter.Value is null or DBNull)
            {
                text = ReplacePlaceholder(text, name, "NULL");
                continue;
            }

            var bound = command.CreateParameter();
            bound.ParameterName = name;
            bound.Value = parameter.Value;
            command.Parameters.Add(bound);
            text = ReplacePlaceholder(text, name, bound.QueryForm);
        }

        command.CommandText = text;
    }

    // \b after the name keeps @p1 from matching inside @p10; a MatchEvaluator avoids the $-substitution
    // rules that a plain replacement string would be subject to.
    private static string ReplacePlaceholder(string text, string name, string replacement) =>
        Regex.Replace(text, $@"@{Regex.Escape(name)}\b", _ => replacement);

    // ClickHouse has no schema layer: a database IS the namespace. The tree is
    // Database → (Tables|Views folder) → Table|View → Columns/Indexes, all read from the system database.
    // No procedures, functions or triggers exist to model.
    public async Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var parent = ancestors.Count == 0 ? (DbNodeKind?)null : ancestors[^1].Kind;

        return parent switch
        {
            // Root: the databases plus a server-wide Security group (users are not per-database).
            null => [.. await LoadDatabasesAsync(profile, ct), new DbTreeNode { Kind = DbNodeKind.Group, Name = "Security", HasChildren = true }],
            DbNodeKind.Group => [new DbTreeNode { Kind = DbNodeKind.UserFolder, Name = "Users", HasChildren = true }],
            DbNodeKind.UserFolder => await LoadUsersAsync(profile, ct),
            DbNodeKind.Database => await FoldersAsync(profile, ancestors, ct),
            DbNodeKind.TableFolder => await LoadRelationsAsync(profile, ancestors, isView: false, ct),
            DbNodeKind.ViewFolder => await LoadRelationsAsync(profile, ancestors, isView: true, ct),
            // A table carries its data-skipping indexes; a view has neither those nor a Columns folder of
            // its own (its columns hang directly under it, as in the MySQL provider).
            DbNodeKind.Table => [ColumnFolder(), IndexFolder()],
            DbNodeKind.ColumnFolder => await LoadColumnsAsync(profile, ancestors.Take(ancestors.Count - 1).ToList(), ct),
            DbNodeKind.View => await LoadColumnsAsync(profile, ancestors, ct),
            DbNodeKind.IndexFolder => await LoadIndexesAsync(profile, ancestors, ct),
            _ => []
        };
    }

    private static DbTreeNode ColumnFolder() =>
        new() { Kind = DbNodeKind.ColumnFolder, Name = "Columns", HasChildren = true };

    private static DbTreeNode IndexFolder() =>
        new() { Kind = DbNodeKind.IndexFolder, Name = "Indexes", HasChildren = true };

    private static DbTreeNode Folder(DbNodeKind kind, string name, int count) =>
        new() { Kind = kind, Name = name, Count = count, HasChildren = count > 0 };

    // ClickHouse's "is this a view" test is the table's engine, not a table_type column.
    private const string ViewEngines = "('View', 'MaterializedView', 'LiveView', 'WindowView')";

    private static readonly HashSet<string> SystemDatabases =
        new(StringComparer.OrdinalIgnoreCase) { "system", "information_schema", "INFORMATION_SCHEMA" };

    private async Task<IReadOnlyList<DbTreeNode>> LoadDatabasesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        // All databases here (system ones flagged) — the host decides whether to show them. The query-tab
        // switcher's GetDatabasesAsync stays user-only.
        const string sql = "SELECT name FROM system.databases ORDER BY name";

        var sizes = await LoadDatabaseSizesAsync(profile, ct);
        var nodes = new List<DbTreeNode>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            nodes.Add(new DbTreeNode
            {
                Kind = DbNodeKind.Database,
                Name = name,
                HasChildren = true,
                IsSystem = SystemDatabases.Contains(name),
                Badge = sizes.TryGetValue(name, out var bytes) ? ByteSize.Format(bytes) : null
            });
        }

        return nodes;
    }

    // Per-database on-disk size from the active parts. Best-effort: a restricted user cannot read
    // system.parts, and then the tree simply carries no size badges.
    private async Task<IReadOnlyDictionary<string, long>> LoadDatabaseSizesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        const string sql = """
            SELECT database, sum(bytes_on_disk)
            FROM system.parts
            WHERE active
            GROUP BY database
            """;

        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            await using var connection = await OpenAsync(profile, ct);
            await using var command = connection.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(1))
                {
                    sizes[reader.GetString(0)] = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                }
            }
        }
        catch
        {
            // No access → no badges.
        }

        return sizes;
    }

    // Database folders with child counts ("Tables (22)"), so the size shows without expanding.
    private async Task<IReadOnlyList<DbTreeNode>> FoldersAsync(
        ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct)
    {
        // $$ raw string: {{…}} interpolates, so ClickHouse's own {name:Type} placeholders stay literal.
        var sql = $$"""
            SELECT engine IN {{ViewEngines}} AS is_view, count()
            FROM system.tables
            WHERE database = {db:String}
            GROUP BY is_view
            """;

        int tables = 0, views = 0;
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand(sql);
        AddParameter(command, "db", Name(ancestors, DbNodeKind.Database));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var count = (int)Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture) == 1)
            {
                views = count;
            }
            else
            {
                tables = count;
            }
        }

        return
        [
            Folder(DbNodeKind.TableFolder, "Tables", tables),
            Folder(DbNodeKind.ViewFolder, "Views", views)
        ];
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadRelationsAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        bool isView,
        CancellationToken ct)
    {
        // total_bytes/total_rows are exact for MergeTree and NULL for engines that do not track them
        // (views, and the log/remote families) — the badge and tooltip are simply omitted there.
        var sql = $$"""
            SELECT name, total_bytes, total_rows
            FROM system.tables
            WHERE database = {db:String} AND engine {{(isView ? "IN" : "NOT IN")}} {{ViewEngines}}
            ORDER BY name
            """;

        var kind = isView ? DbNodeKind.View : DbNodeKind.Table;
        var nodes = new List<DbTreeNode>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand(sql);
        AddParameter(command, "db", Name(ancestors, DbNodeKind.Database));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode
            {
                Kind = kind,
                Name = reader.GetString(0),
                HasChildren = true,
                Badge = reader.IsDBNull(1) ? null : ByteSize.Format(Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)),
                Tooltip = reader.IsDBNull(2) ? null : TableStats.RowTooltip(Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture))
            });
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadColumnsAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        // is_in_primary_key marks the sorting key's columns. ClickHouse's "primary key" is a sparse index,
        // not a uniqueness constraint, so it is shown for orientation only — it is deliberately not fed
        // into ResultColumn.IsKey, which would wrongly advertise the grid as editable.
        const string sql = """
            SELECT name, type, is_in_primary_key
            FROM system.columns
            WHERE database = {db:String} AND table = {tbl:String}
            ORDER BY position
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand(sql);
        AddParameter(command, "db", Name(ancestors, DbNodeKind.Database));
        AddParameter(command, "tbl", ancestors[^1].Name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var type = reader.GetString(1);
            var key = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) == 1 ? " (PK)" : string.Empty;
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Column, Name = reader.GetString(0), Detail = $"{type}{key}" });
        }

        return nodes;
    }

    // ClickHouse's only table-level indexes are data-skipping indexes (minmax, set, bloom_filter, …); there
    // is no b-tree/unique index to list, and no foreign keys at all.
    private static async Task<IReadOnlyList<DbTreeNode>> LoadIndexesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        const string sql = """
            SELECT name, type_full, expr
            FROM system.data_skipping_indices
            WHERE database = {db:String} AND table = {tbl:String}
            ORDER BY name
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand(sql);
        AddParameter(command, "db", Name(ancestors, DbNodeKind.Database));
        AddParameter(command, "tbl", Name(ancestors, DbNodeKind.Table));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode
            {
                Kind = DbNodeKind.Index,
                Name = reader.GetString(0),
                Detail = reader.GetString(1),
                Tooltip = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
        }

        return nodes;
    }

    // SHOW CREATE returns the roundtrip-safe definition for both tables and views (including the engine,
    // ORDER BY and TTL clauses), in a single "statement" column.
    public async Task<string?> GetObjectDefinitionAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        if (ancestors[^1].Kind is not (DbNodeKind.Table or DbNodeKind.View))
        {
            return null;
        }

        var qualified = Dialect.QualifyName(Name(ancestors, DbNodeKind.Database), null, ancestors[^1].Name);
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand($"SHOW CREATE TABLE {qualified}");
        return (await command.ExecuteScalarAsync(ct))?.ToString();
    }

    private static string Name(IReadOnlyList<DbNodeRef> ancestors, DbNodeKind kind) =>
        ancestors.First(a => a.Kind == kind).Name;

    // ClickHouse's own {name:Type} parameter form; the driver infers the type from the value.
    private static void AddParameter(ClickHouseCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    // Re-point the connection at a sibling database on the same server; host/credentials stay intact.
    private static string ConnectionStringFor(ConnectionProfile profile, string database) =>
        new ClickHouseConnectionStringBuilder(profile.ConnectionString) { Database = database }.ConnectionString;

    // Open against the tree's database when the host set ConnectionProfile.Database (query-tab database
    // switcher, DDL on a specific db); otherwise the connection's own default.
    private static async Task<ClickHouseConnection> OpenAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var connectionString = string.IsNullOrWhiteSpace(profile.Database)
            ? profile.ConnectionString
            : ConnectionStringFor(profile, profile.Database);

        var connection = new ClickHouseConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    // No schema layer (a database IS the namespace), so only Database and Table are creatable.
    public IReadOnlyList<CreateCapability> CreateCapabilities { get; } =
    [
        new(DbObjectKind.Database, null),
        new(DbObjectKind.Table, DbNodeKind.TableFolder)
    ];

    public IReadOnlyList<string> ColumnTypes { get; } =
        ["UInt64", "Int32", "Int64", "Float64", "Decimal(18, 2)", "String", "FixedString(16)",
         "Bool", "Date", "DateTime", "DateTime64(3)", "UUID", "Array(String)", "JSON"];

    public SqlStatement BuildCreateStatement(CreateObjectSpec spec)
    {
        var sql = spec.Kind switch
        {
            DbObjectKind.Database => $"CREATE DATABASE {Dialect.QuoteIdentifier(spec.Name)}",
            // No schema layer to qualify with — the connection is already pointed at the target database
            // via ConnectionProfile.Database when this runs (see ExecuteDdlAsync).
            DbObjectKind.Table => BuildCreateTable(spec),
            _ => throw new NotSupportedException($"ClickHouse cannot create a {spec.Kind}.")
        };

        return new SqlStatement(sql, []);
    }

    /// <summary>
    /// Two things make this differ from every other provider's CREATE TABLE. A table engine is mandatory —
    /// MergeTree is the only sane default — and it demands an ORDER BY, which is <c>tuple()</c> (explicitly
    /// "no sorting key") when the user marked no primary-key columns. Nullability is inverted from SQL's
    /// default too: a ClickHouse column is NOT NULL unless its type is wrapped in <c>Nullable(…)</c>.
    /// <c>AutoIncrement</c> has no equivalent and is ignored — the user's own DEFAULT expression (e.g.
    /// <c>generateUUIDv4()</c>) is the ClickHouse idiom, and the generated DDL is editable before it runs.
    /// </summary>
    private string BuildCreateTable(CreateObjectSpec spec)
    {
        var columns = spec.Columns.Select(c =>
            $"{Dialect.QuoteIdentifier(c.Name)} {NullableType(c.Type, c.Nullable)}");

        var primaryKey = spec.Columns.Where(c => c.PrimaryKey).Select(c => Dialect.QuoteIdentifier(c.Name)).ToList();
        var orderBy = primaryKey.Count > 0 ? $"({string.Join(", ", primaryKey)})" : "tuple()";

        return $"CREATE TABLE {Dialect.QuoteIdentifier(spec.Name)} ({string.Join(", ", columns)})\n" +
               $"ENGINE = MergeTree\nORDER BY {orderBy}";
    }

    // A sorting-key column cannot be Nullable in MergeTree, but that is the server's rule to enforce — the
    // generated DDL stays a faithful rendering of what the user asked for, and a bad combination fails as
    // an ordinary DDL error the user can fix in the preview.
    private static string NullableType(string type, bool nullable) =>
        nullable && !type.StartsWith("Nullable(", StringComparison.OrdinalIgnoreCase)
            ? $"Nullable({type})"
            : type;

    public async Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand($"EXPLAIN {sql}");
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await ReadResultAsync(reader, stopwatch, ct);
    }

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        const string sql = """
            SELECT name FROM system.databases
            WHERE name NOT IN ('system', 'information_schema', 'INFORMATION_SCHEMA')
            ORDER BY name
            """;

        var names = new List<string>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    // Activity Monitor. system.processes is the live query list; queryID() identifies the monitor's own
    // query for the visible-but-disabled guard.
    //
    // ClickHouse has only one kill verb, KILL QUERY — the HTTP interface is stateless, so there is no
    // session or connection to terminate. KillSessionAsync therefore cancels the running query, and
    // SupportsCancelQuery stays false rather than offering a second, identical action.
    public bool SupportsActivityMonitor => true;

    public string SessionIdColumn => "query_id";

    public async Task<ActiveSessionSnapshot> GetActiveSessionsAsync(ConnectionProfile profile, CancellationToken ct)
    {
        const string sql = """
            SELECT query_id, user, address, elapsed, read_rows, memory_usage, query
            FROM system.processes
            ORDER BY elapsed DESC
            """;

        var stopwatch = Stopwatch.StartNew();
        await using var connection = await OpenAsync(profile, ct);

        string? currentId;
        await using (var id = connection.CreateCommand("SELECT queryID()"))
        {
            currentId = (await id.ExecuteScalarAsync(ct))?.ToString();
        }

        await using var command = connection.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = await ReadResultAsync(reader, stopwatch, ct);
        return new ActiveSessionSnapshot(result, currentId);
    }

    public async Task KillSessionAsync(ConnectionProfile profile, string sessionId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        // KILL QUERY takes a WHERE clause over system.processes, not a parameter marker, and the driver's
        // {name:Type} substitution does not apply inside it — so the id goes in as an escaped literal.
        await using var command = connection.CreateCommand(
            $"KILL QUERY WHERE query_id = '{sessionId.Replace("\\", "\\\\").Replace("'", "\\'")}'");
        await command.ExecuteNonQueryAsync(ct);
    }

    // User management. ClickHouse users are server-wide (system.users) and roles are a first-class object,
    // so both map straight onto the host's generic Create/Drop user flow. Note this only works when the
    // connected user has access management rights — the ContainerRecipe turns those on for a local
    // container via CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT.
    private async Task<IReadOnlyList<DbTreeNode>> LoadUsersAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var nodes = new List<DbTreeNode>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand("SELECT name FROM system.users ORDER BY name");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.User, Name = reader.GetString(0) });
        }

        return nodes;
    }

    public bool CanManageUsers => true;

    public IReadOnlyList<UserField> UserFields { get; } =
    [
        new("password", "Password", UserFieldType.Password, Required: true)
    ];

    public async Task<IReadOnlyList<string>> GetAssignableRolesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var roles = new List<string>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand("SELECT name FROM system.roles ORDER BY name");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            roles.Add(reader.GetString(0));
        }

        return roles;
    }

    public SqlStatement BuildCreateUserStatement(IReadOnlyDictionary<string, string?> values, IReadOnlyList<string> roles)
    {
        var name = Dialect.QuoteIdentifier(values["name"] ?? string.Empty);
        var password = (values.GetValueOrDefault("password") ?? string.Empty)
            .Replace("\\", "\\\\").Replace("'", "\\'");

        var script = new StringBuilder();
        script.Append($"CREATE USER {name} IDENTIFIED BY '{password}';");
        if (roles.Count > 0)
        {
            var granted = string.Join(", ", roles.Select(Dialect.QuoteIdentifier));
            script.Append($"\nGRANT {granted} TO {name};");
        }

        return new SqlStatement(script.ToString(), []);
    }

    public SqlStatement BuildDropUserStatement(DbNodeRef userNode, IReadOnlyList<DbNodeRef> ancestors) =>
        new($"DROP USER {Dialect.QuoteIdentifier(userNode.Name)};", []);
}
