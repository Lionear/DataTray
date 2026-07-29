using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using DataTray.Sdk;
using DuckDB.NET.Data;

namespace DataTray.Providers.DuckDb;

/// <summary>
/// A DuckDB provider (SE-12). DuckDB is an embedded, columnar SQL engine, so in shape it is the SQLite
/// provider — one file, in-process, no server — and it needs no SDK change. Four things about the driver
/// and the engine do not follow the pattern the other SQL providers set, each verified against DuckDB
/// 1.5.5 rather than assumed:
/// <list type="bullet">
/// <item><b>Parameters are <c>$name</c>, not <c>@name</c>.</b> The host generates one shape of
/// parameterised SQL for every SQL provider; DuckDB answers <c>@p0</c> with
/// <c>Binder Error: Referenced column "p0" was not found</c>, so <see cref="Bind"/> rewrites the markers
/// and registers the values under the unprefixed name (a <c>$</c>-prefixed <c>ParameterName</c> is
/// rejected in turn).</item>
/// <item><b>The cancellation token is ignored, but <c>Cancel()</c> works.</b> A token passed to
/// <c>ExecuteScalarAsync</c> left a 65-second query running to completion; <c>DuckDBCommand.Cancel()</c>
/// from another thread aborted the same query in 600 ms. Every execution path here therefore bridges the
/// two (<see cref="Bridge"/>) — without it an analytics engine, the one most likely to be handed a
/// runaway query, would be the only provider whose Cancel button does nothing.</item>
/// <item><b>The reader carries no base-table or primary-key metadata</b>, which is what the editable grid
/// runs on. <see cref="DuckDbBrowseTarget"/> recovers the table from the query text and this provider
/// introspects its primary key, so a single-table browse stays editable and anything more complex is
/// read-only.</item>
/// <item><b>The <c>main</c> schema reports <c>internal = true</c></b> in <c>duckdb_schemas()</c>, the same
/// flag that marks <c>pg_catalog</c>. Filtering on it — the obvious reading — hides the one schema almost
/// every DuckDB file actually uses, so the tree filters on the catalog instead.</item>
/// </list>
/// The reason to reach for DuckDB is that a query can read a file directly
/// (<c>SELECT * FROM 'events.parquet'</c>), which needs nothing from this provider beyond not getting in
/// the way — such a result set simply comes back read-only.
/// </summary>
public sealed class DuckDbProvider : IDbProvider
{
    public string DisplayName => "DuckDB";

    // Uses the embedded brand PNG (icon.png) when present; falls back to a glyph otherwise.
    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(DuckDbProvider), "🦆");

    public ISqlDialect Dialect { get; } = new DuckDbDialect();

    // Embedded engine: there is no server to containerise, so no recipe — the same "null = not
    // supported" answer the file-based SQLite provider gives.
    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("path", "Database file", ConnectionFieldType.File,
            Placeholder: "/path/to/analytics.duckdb"),
        // An in-memory database is DuckDB's scratchpad for querying files without creating one
        // (SELECT * FROM 'data.parquet'). It overrides the path rather than sitting beside it, so it is
        // the one case where a blank file path is still a valid connection.
        new("inMemory", "In-memory database (no file)", ConnectionFieldType.Bool, Default: "false",
            Group: "Advanced", Advanced: true)
    ];

    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values)
    {
        var inMemory = values.TryGetValue("inMemory", out var flag)
            && bool.TryParse(flag, out var parsed) && parsed;

        var path = values.TryGetValue("path", out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        return new DuckDBConnectionStringBuilder
        {
            DataSource = inMemory || path is null ? ":memory:" : path
        }.ConnectionString;
    }

    // Inverse of BuildConnectionString. DuckDBConnectionStringBuilder exposes DataSource alone (it drops
    // any other keyword), so ":memory:" is what distinguishes the two modes on the way back in.
    public IReadOnlyDictionary<string, string?>? ParseConnectionString(string connectionString)
    {
        // DuckDBConnectionStringBuilder has no connection-string constructor; the base class's
        // settable ConnectionString is the way in.
        var b = new DuckDBConnectionStringBuilder { ConnectionString = connectionString };
        var result = new Dictionary<string, string?>();

        if (string.Equals(b.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            result["inMemory"] = "true";
        }
        else if (!string.IsNullOrWhiteSpace(b.DataSource))
        {
            result["path"] = b.DataSource;
        }

        return result;
    }

    public async Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        return connection.State == ConnectionState.Open;
    }

    // DuckDB.NET reports the library version (e.g. "v1.5.5") on the open connection — no round-trip.
    public async Task<string?> GetServerVersionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        return string.IsNullOrWhiteSpace(connection.ServerVersion) ? null : connection.ServerVersion;
    }

    private static async Task<DuckDBConnection> OpenAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var connection = new DuckDBConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>
    /// Makes <paramref name="ct"/> actually stop <paramref name="command"/>. DuckDB.NET accepts a token on
    /// its async methods and ignores it — a cancelled token left a 65-second query running to the end —
    /// while <c>Cancel()</c> called from another thread aborts within milliseconds. The registration is
    /// disposed with the returned handle, so it never outlives the command.
    /// </summary>
    private static CancellationTokenRegistration Bridge(DuckDBCommand command, CancellationToken ct) =>
        ct.Register(static state => ((DuckDBCommand)state!).Cancel(), command);

    /// <summary>
    /// Runs <paramref name="body"/> on a pool thread. Needed because DuckDB.NET's <c>…Async</c> overloads
    /// are synchronous underneath — timing the phases of a 70-second query showed all 70 seconds inside
    /// <c>ExecuteReaderAsync</c> itself, with the awaits never yielding — while the host awaits a provider
    /// directly on the UI thread (<c>DocumentViewModel.RunTracked</c>), which every other driver here can
    /// afford because its async really is async.
    /// <para>Without this the UI would freeze for the whole query and, worse, nothing could cancel it: the
    /// Stop button and the query-timeout both act on a token whose <c>CancelAfter</c> is scheduled by the
    /// same thread the driver would be blocking. Offloading gives that thread back, and
    /// <see cref="Bridge"/> then turns the token into the <c>Cancel()</c> the driver does honour.</para>
    /// <para><see cref="CancellationToken.None"/> is passed to <c>Task.Run</c> on purpose: cancellation must
    /// interrupt the running query through <see cref="Bridge"/>, not prevent the work from being scheduled
    /// (which would leave the connection unopened and report a cancel that never reached the engine).</para>
    /// </summary>
    private static Task<T> OffloadAsync<T>(Func<Task<T>> body) => Task.Run(body, CancellationToken.None);

    public Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct) =>
        OffloadAsync(async () =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            await using var connection = await OpenAsync(profile, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var bridge = Bridge(command, ct);

            await using var reader = await command.ExecuteReaderAsync(ct);
            var keys = await ResolveKeyColumnsAsync(connection, sql, ct);
            return await ReadResultAsync(reader, keys, stopwatch, ct);
        });

    public Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct) =>
        OffloadAsync<IReadOnlyList<QueryResult>>(async () =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            await using var connection = await OpenAsync(profile, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var bridge = Bridge(command, ct);

            // Unlike ClickHouse, DuckDB.NET walks a multi-statement batch natively via NextResult, so the
            // host's ';'-joined script needs no splitting here.
            await using var reader = await command.ExecuteReaderAsync(ct);

            var results = new List<QueryResult>();
            do
            {
                results.Add(await ReadResultAsync(reader, keys: null, stopwatch, ct));
            } while (await reader.NextResultAsync(ct));

            return results;
        });

    private static async Task<QueryResult> ReadResultAsync(
        DbDataReader reader, KeyColumns? keys, System.Diagnostics.Stopwatch stopwatch, CancellationToken ct)
    {
        var columns = BuildColumns(reader, keys);

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

    /// <summary>The table and primary-key columns a result set writes back to.</summary>
    private sealed record KeyColumns(string? Schema, string Table, IReadOnlySet<string> Keys);

    /// <summary>
    /// The edit metadata the driver does not supply: recover the single table the query reads
    /// (<see cref="DuckDbBrowseTarget"/>), then look up its primary key. Returns null — leaving the grid
    /// read-only — when the query is anything more than one table's rows, or when that table has no
    /// primary key to identify a row by.
    /// </summary>
    private static async Task<KeyColumns?> ResolveKeyColumnsAsync(
        DuckDBConnection connection, string sql, CancellationToken ct)
    {
        if (DuckDbBrowseTarget.From(sql) is not { } target)
        {
            return null;
        }

        // constraint_column_names is a LIST(VARCHAR), which the driver surfaces as a List<string>.
        const string pkSql = """
            SELECT constraint_column_names
            FROM duckdb_constraints()
            WHERE table_name = $tbl
              AND ($schema IS NULL OR schema_name = $schema)
              AND constraint_type = 'PRIMARY KEY'
            """;

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = pkSql;
            command.Parameters.Add(new DuckDBParameter("tbl", target.Table));
            command.Parameters.Add(new DuckDBParameter("schema", (object?)target.Schema ?? DBNull.Value));
            using var bridge = Bridge(command, ct);

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.IsDBNull(0))
            {
                return null;
            }

            var names = (reader.GetValue(0) as System.Collections.IEnumerable)?
                .Cast<object?>()
                .Select(n => n?.ToString())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return names is { Count: > 0 } ? new KeyColumns(target.Schema, target.Table, names!) : null;
        }
        catch
        {
            // Introspection is an enhancement, never a reason to fail the query the user asked for.
            return null;
        }
    }

    // Name and CLR type always; the base-table/key metadata only when ResolveKeyColumnsAsync traced the
    // result to one table (see the class remarks). A column is marked IsKey only if that table's primary
    // key actually contains it, so a browse that projects the key away stays read-only rather than
    // pretending a row is addressable.
    private static List<ResultColumn> BuildColumns(DbDataReader reader, KeyColumns? keys)
    {
        var columns = new List<ResultColumn>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            columns.Add(new ResultColumn(name, reader.GetFieldType(i))
            {
                BaseSchema = keys?.Schema,
                BaseTable = keys?.Table,
                BaseColumn = keys is null ? null : name,
                IsKey = keys?.Keys.Contains(name) ?? false
            });
        }

        // Every key column must be present for the host to address a row; if the projection dropped one,
        // drop the edit metadata entirely rather than offering a half-usable grid.
        if (keys is not null && !keys.Keys.All(k => columns.Any(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))))
        {
            return [.. columns.Select(c => new ResultColumn(c.Name, c.ClrType))];
        }

        return columns;
    }

    public Task<int> ExecuteBatchAsync(
        ConnectionProfile profile,
        IReadOnlyList<SqlStatement> statements,
        CancellationToken ct) =>
        OffloadAsync(async () =>
        {
            await using var connection = await OpenAsync(profile, ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            var affected = 0;
            foreach (var statement in statements)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (DuckDBTransaction)transaction;
                Bind(command, statement);
                using var bridge = Bridge(command, ct);
                affected += await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return affected;
        });

    /// <summary>
    /// Rewrites the host's <c>@name</c> placeholders into DuckDB's <c>$name</c> form and binds the values
    /// under the unprefixed name. Both halves are load-bearing and were established by testing: <c>@p0</c>
    /// in the SQL fails to bind at all, and naming the parameter <c>$p0</c> instead of <c>p0</c> fails with
    /// "Values were not provided for the following prepared statement parameters". DuckDB itself
    /// distinguishes <c>$p1</c> from <c>$p10</c> correctly; the <c>\b</c> below is what stops the textual
    /// rewrite from conflating them first.
    /// </summary>
    private static void Bind(DuckDBCommand command, SqlStatement statement)
    {
        var text = statement.Text;

        foreach (var parameter in statement.Parameters)
        {
            var name = parameter.Name.TrimStart('@');
            text = Regex.Replace(text, $@"@{Regex.Escape(name)}\b", _ => "$" + name);
            command.Parameters.Add(new DuckDBParameter(name, parameter.Value ?? DBNull.Value));
        }

        command.CommandText = text;
    }

    // DuckDB has catalog → schema → object. One connection is one file, so the catalog is implicit and the
    // tree starts at its schemas: Schemas → schema → (Tables|Views|Sequences) → object → Columns/Indexes.
    // No stored procedures, functions or triggers exist to model.
    public Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct) =>
        // Introspection queries are small, but they are executed by the same blocking driver, so they take
        // the offloaded path too rather than stalling the UI thread on every tree expansion.
        OffloadAsync(() => ChildNodesAsync(profile, ancestors, ct));

    private static async Task<IReadOnlyList<DbTreeNode>> ChildNodesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var parent = ancestors.Count == 0 ? (DbNodeKind?)null : ancestors[^1].Kind;

        return parent switch
        {
            null => [SchemaFolder()],
            DbNodeKind.SchemaFolder => await LoadSchemasAsync(profile, ct),
            DbNodeKind.Schema => await FoldersAsync(profile, ancestors, ct),
            DbNodeKind.TableFolder => await LoadRelationsAsync(profile, ancestors, isView: false, ct),
            DbNodeKind.ViewFolder => await LoadRelationsAsync(profile, ancestors, isView: true, ct),
            DbNodeKind.SequenceFolder => await LoadSequencesAsync(profile, ancestors, ct),
            // A table carries its indexes; a view has none, and its columns hang directly under it.
            DbNodeKind.Table => [ColumnFolder(), IndexFolder()],
            DbNodeKind.ColumnFolder => await LoadColumnsAsync(profile, ancestors.Take(ancestors.Count - 1).ToList(), ct),
            DbNodeKind.View => await LoadColumnsAsync(profile, ancestors, ct),
            DbNodeKind.IndexFolder => await LoadIndexesAsync(profile, ancestors, ct),
            _ => []
        };
    }

    private static DbTreeNode SchemaFolder() =>
        new() { Kind = DbNodeKind.SchemaFolder, Name = "Schemas", HasChildren = true };

    private static DbTreeNode ColumnFolder() =>
        new() { Kind = DbNodeKind.ColumnFolder, Name = "Columns", HasChildren = true };

    private static DbTreeNode IndexFolder() =>
        new() { Kind = DbNodeKind.IndexFolder, Name = "Indexes", HasChildren = true };

    private static DbTreeNode Folder(DbNodeKind kind, string name, int count) =>
        new() { Kind = kind, Name = name, Count = count, HasChildren = count > 0 };

    /// <summary>
    /// The schemas of the connected file. Filtering on <c>duckdb_schemas().internal</c> looks like the
    /// right move and is not: <c>main</c> — the schema essentially every DuckDB file uses — carries the
    /// same <c>internal = true</c> as <c>pg_catalog</c>, so that filter would leave the tree empty. What
    /// actually separates the user's schemas from the built-ins is the catalog: the connected database
    /// versus the <c>system</c> and <c>temp</c> ones.
    /// </summary>
    private static async Task<IReadOnlyList<DbTreeNode>> LoadSchemasAsync(ConnectionProfile profile, CancellationToken ct)
    {
        const string sql = """
            SELECT schema_name
            FROM duckdb_schemas()
            WHERE database_name = current_database()
            ORDER BY schema_name
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var bridge = Bridge(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            nodes.Add(new DbTreeNode
            {
                Kind = DbNodeKind.Schema,
                Name = name,
                HasChildren = true,
                IsSystem = string.Equals(name, "information_schema", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase)
            });
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> FoldersAsync(
        ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct)
    {
        var schema = Name(ancestors, DbNodeKind.Schema);
        int tables = 0, views = 0, sequences = 0;

        await using var connection = await OpenAsync(profile, ct);

        async Task<int> CountAsync(string function)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT count(*) FROM {function} WHERE database_name = current_database() AND schema_name = $schema";
            command.Parameters.Add(new DuckDBParameter("schema", schema));
            using var bridge = Bridge(command, ct);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        tables = await CountAsync("duckdb_tables()");
        views = await CountAsync("duckdb_views()");
        sequences = await CountAsync("duckdb_sequences()");

        return
        [
            Folder(DbNodeKind.TableFolder, "Tables", tables),
            Folder(DbNodeKind.ViewFolder, "Views", views),
            Folder(DbNodeKind.SequenceFolder, "Sequences", sequences)
        ];
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadRelationsAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        bool isView,
        CancellationToken ct)
    {
        // duckdb_tables() carries estimated_size (rows, not bytes — DuckDB exposes no per-table on-disk
        // size) and column_count; duckdb_views() has neither, so a view gets no badge.
        var sql = isView
            ? """
              SELECT view_name, NULL, NULL
              FROM duckdb_views()
              WHERE database_name = current_database() AND schema_name = $schema
              ORDER BY view_name
              """
            : """
              SELECT table_name, estimated_size, column_count
              FROM duckdb_tables()
              WHERE database_name = current_database() AND schema_name = $schema
              ORDER BY table_name
              """;

        var nodes = new List<DbTreeNode>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new DuckDBParameter("schema", Name(ancestors, DbNodeKind.Schema)));
        using var bridge = Bridge(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode
            {
                Kind = isView ? DbNodeKind.View : DbNodeKind.Table,
                Name = reader.GetString(0),
                HasChildren = true,
                Tooltip = reader.IsDBNull(1)
                    ? null
                    : TableStats.RowTooltip(Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)),
                Detail = reader.IsDBNull(2)
                    ? null
                    : $"{Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture)} cols"
            });
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadColumnsAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        // information_schema gives ordering and nullability; the primary key comes from
        // duckdb_constraints(), the same source the editable grid's key resolution uses.
        const string sql = """
            SELECT column_name, data_type, is_nullable
            FROM information_schema.columns
            WHERE table_catalog = current_database() AND table_schema = $schema AND table_name = $tbl
            ORDER BY ordinal_position
            """;

        const string pkSql = """
            SELECT unnest(constraint_column_names)
            FROM duckdb_constraints()
            WHERE database_name = current_database() AND schema_name = $schema
              AND table_name = $tbl AND constraint_type = 'PRIMARY KEY'
            """;

        var schema = Name(ancestors, DbNodeKind.Schema);
        var table = ancestors[^1].Name;

        await using var connection = await OpenAsync(profile, ct);

        var primaryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pk = connection.CreateCommand())
        {
            pk.CommandText = pkSql;
            pk.Parameters.Add(new DuckDBParameter("schema", schema));
            pk.Parameters.Add(new DuckDBParameter("tbl", table));
            using var bridge = Bridge(pk, ct);
            await using var pkReader = await pk.ExecuteReaderAsync(ct);
            while (await pkReader.ReadAsync(ct))
            {
                if (!pkReader.IsDBNull(0))
                {
                    primaryKeys.Add(pkReader.GetString(0));
                }
            }
        }

        var nodes = new List<DbTreeNode>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.Parameters.Add(new DuckDBParameter("schema", schema));
            command.Parameters.Add(new DuckDBParameter("tbl", table));
            using var bridge = Bridge(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var type = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var nullable = !reader.IsDBNull(2) && reader.GetString(2) == "YES" ? string.Empty : " NOT NULL";
                var key = primaryKeys.Contains(name) ? " (PK)" : string.Empty;
                nodes.Add(new DbTreeNode { Kind = DbNodeKind.Column, Name = name, Detail = $"{type}{nullable}{key}" });
            }
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadIndexesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        const string sql = """
            SELECT index_name, is_unique, sql
            FROM duckdb_indexes()
            WHERE database_name = current_database() AND schema_name = $schema AND table_name = $tbl
            ORDER BY index_name
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new DuckDBParameter("schema", Name(ancestors, DbNodeKind.Schema)));
        command.Parameters.Add(new DuckDBParameter("tbl", Name(ancestors, DbNodeKind.Table)));
        using var bridge = Bridge(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode
            {
                Kind = DbNodeKind.Index,
                Name = reader.GetString(0),
                Detail = !reader.IsDBNull(1) && reader.GetBoolean(1) ? "unique" : null,
                Tooltip = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadSequencesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        const string sql = """
            SELECT sequence_name, last_value
            FROM duckdb_sequences()
            WHERE database_name = current_database() AND schema_name = $schema
            ORDER BY sequence_name
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new DuckDBParameter("schema", Name(ancestors, DbNodeKind.Schema)));
        using var bridge = Bridge(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // last_value is NULL until the sequence has been drawn from at least once.
            nodes.Add(new DbTreeNode
            {
                Kind = DbNodeKind.Sequence,
                Name = reader.GetString(0),
                Detail = reader.IsDBNull(1) ? null : $"= {reader.GetValue(1)}"
            });
        }

        return nodes;
    }

    // View Definition: duckdb_tables()/duckdb_views() both carry the object's own CREATE text, so a table
    // or view definition needs no reconstruction.
    public async Task<string?> GetObjectDefinitionAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var (function, column) = ancestors[^1].Kind switch
        {
            DbNodeKind.Table => ("duckdb_tables()", "table_name"),
            DbNodeKind.View => ("duckdb_views()", "view_name"),
            _ => (null, null)
        };

        if (function is null)
        {
            return null;
        }

        await using var connection = await OpenAsync(profile, ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT sql FROM {function} WHERE database_name = current_database() AND schema_name = $schema AND {column} = $name";
        command.Parameters.Add(new DuckDBParameter("schema", Name(ancestors, DbNodeKind.Schema)));
        command.Parameters.Add(new DuckDBParameter("name", ancestors[^1].Name));
        using var bridge = Bridge(command, ct);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static string Name(IReadOnlyList<DbNodeRef> ancestors, DbNodeKind kind) =>
        ancestors.First(a => a.Kind == kind).Name;

    // DuckDB has no CREATE DATABASE (a second file is ATTACHed, not created), so schema and table are
    // what this provider offers.
    public IReadOnlyList<CreateCapability> CreateCapabilities { get; } =
    [
        new(DbObjectKind.Schema, DbNodeKind.SchemaFolder),
        new(DbObjectKind.Table, DbNodeKind.TableFolder)
    ];

    public IReadOnlyList<string> ColumnTypes { get; } =
        ["INTEGER", "BIGINT", "DOUBLE", "DECIMAL(18, 2)", "VARCHAR", "BOOLEAN", "DATE",
         "TIMESTAMP", "TIMESTAMPTZ", "UUID", "BLOB", "JSON", "VARCHAR[]"];

    public SqlStatement BuildCreateStatement(CreateObjectSpec spec)
    {
        var sql = spec.Kind switch
        {
            DbObjectKind.Schema => $"CREATE SCHEMA {Dialect.QuoteIdentifier(spec.Name)}",
            DbObjectKind.Table => BuildCreateTable(spec),
            _ => throw new NotSupportedException($"DuckDB cannot create a {spec.Kind}.")
        };

        return new SqlStatement(sql, []);
    }

    // Postgres-shaped DDL, with one DuckDB-specific choice: there is no AUTO_INCREMENT/IDENTITY keyword,
    // so an auto-increment column becomes a sequence plus a DEFAULT that draws from it — which is how the
    // DuckDB documentation itself models the pattern. The generated script stays editable in the preview.
    private string BuildCreateTable(CreateObjectSpec spec)
    {
        var qualified = Dialect.QualifyName(null, spec.Schema, spec.Name);
        var autoIncrement = spec.Columns.FirstOrDefault(c => c.AutoIncrement);
        var sequence = autoIncrement is null
            ? null
            : Dialect.QualifyName(null, spec.Schema, $"seq_{spec.Name}_{autoIncrement.Name}");

        var columns = spec.Columns.Select(c =>
        {
            var definition = $"{Dialect.QuoteIdentifier(c.Name)} {c.Type}";
            if (c == autoIncrement)
            {
                definition += $" DEFAULT nextval('{sequence!.Replace("'", "''")}')";
            }

            return definition + (c.Nullable ? string.Empty : " NOT NULL");
        });

        var primaryKey = spec.Columns.Where(c => c.PrimaryKey).Select(c => Dialect.QuoteIdentifier(c.Name)).ToList();
        var clauses = primaryKey.Count > 0
            ? columns.Append($"PRIMARY KEY ({string.Join(", ", primaryKey)})")
            : columns;

        var create = $"CREATE TABLE {qualified} ({string.Join(", ", clauses)})";
        return sequence is null ? create : $"CREATE SEQUENCE {sequence};\n{create}";
    }

    // DDL can be arbitrarily long here — CREATE TABLE AS over a large file is the normal way to load data
    // into DuckDB — so it takes the same offloaded, cancellable path as a query.
    public Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct) =>
        OffloadAsync(async () =>
        {
            await using var connection = await OpenAsync(profile, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var bridge = Bridge(command, ct);
            await command.ExecuteNonQueryAsync(ct);
            return true;
        });

    public Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct) =>
        OffloadAsync(async () =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await using var connection = await OpenAsync(profile, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"EXPLAIN {sql}";
            using var bridge = Bridge(command, ct);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await ReadResultAsync(reader, keys: null, stopwatch, ct);
        });

    // One connection is one file, and DuckDB cannot be re-pointed at another catalog through the
    // connection string (a second file is ATTACHed inside the session), so there is nothing for the
    // query-tab database switcher to offer — the same answer SQLite gives.
    public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
