using DataTray.Sdk;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>What an index tool does to its target. Mirrors SSMS's menu on a table's Indexes node. Public
/// only because the tool classes the host loads are public and declare it in a protected member.</summary>
public enum IndexAction
{
    Rebuild,
    Reorganize,
    Disable,
    Drop
}

/// <summary>
/// Builds the one statement each index action runs. Pure string work over quoted identifiers, so it is
/// tested directly — this is where a wrong name silently maintains the wrong object.
/// </summary>
/// <remarks>
/// Rebuilding a <b>filtered</b> index requires <c>SET QUOTED_IDENTIFIER ON</c>, and fails with a message
/// about indexed views and computed columns that mentions no such thing as a filter. SqlClient sets that
/// option on by default, so the statements below need no preamble — but a port to a client that does not
/// (sqlcmd, for one) would fail on exactly the tables most worth maintaining.
/// </remarks>
internal static class IndexStatements
{
    /// <summary>
    /// The statement for <paramref name="action"/> on <paramref name="index"/>, or on every index of the
    /// table when <paramref name="index"/> is null (SSMS's "Rebuild All" on the Indexes folder).
    /// </summary>
    public static string Build(ISqlDialect dialect, IndexAction action, string? schema, string table, string? index)
    {
        var qualified = string.IsNullOrEmpty(schema)
            ? dialect.QuoteIdentifier(table)
            : $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(table)}";

        // ALTER INDEX takes ALL as a bare keyword, never quoted — quoting it would name an index called
        // "ALL" instead of meaning every index.
        var target = index is { Length: > 0 } ? dialect.QuoteIdentifier(index) : "ALL";

        if (action == IndexAction.Drop)
        {
            if (index is not { Length: > 0 })
            {
                // DROP INDEX ALL does not exist, and dropping every index of a table is not something to
                // synthesise as a loop behind one menu click.
                throw new InvalidOperationException("Dropping every index at once is not supported.");
            }

            return $"DROP INDEX {target} ON {qualified}";
        }

        var verb = action switch
        {
            IndexAction.Rebuild => "REBUILD",
            IndexAction.Reorganize => "REORGANIZE",
            _ => "DISABLE"
        };

        return $"ALTER INDEX {target} ON {qualified} {verb}";
    }

    /// <summary>
    /// Whether this index backs a primary key or a unique constraint. Those cannot be dropped with DROP
    /// INDEX — SQL Server answers with "an explicit DROP INDEX is not allowed on index …", which reads as a
    /// permissions problem rather than as "this is a constraint, drop the constraint".
    /// </summary>
    public static string ConstraintCheck(ISqlDialect dialect, string? schema, string table, string index)
    {
        // OBJECT_ID takes the name as text, so the identifiers are bracket-quoted inside the literal: a
        // table called "my.table" would otherwise read as schema "my", table "table".
        var qualified = string.IsNullOrEmpty(schema)
            ? dialect.QuoteIdentifier(table)
            : $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(table)}";

        return $"""
            SELECT i.is_primary_key, i.is_unique_constraint
            FROM sys.indexes AS i
            WHERE i.object_id = OBJECT_ID({Literal(qualified)}) AND i.name = {Literal(index)}
            """;
    }

    /// <summary>
    /// The table's indexes with their current fragmentation and size, for the dialog to show before the
    /// user confirms — SSMS puts the same numbers in front of a bulk rebuild, because "which of these are
    /// actually fragmented?" is the whole question a rebuild answers. Narrowed to one index when
    /// <paramref name="index"/> is given.
    /// </summary>
    /// <remarks>
    /// <c>LIMITED</c> is the cheap mode: it reads the parent level of the b-tree rather than every page, so
    /// opening this dialog on a large table costs a scan of the index, not of the data. It still returns
    /// both numbers shown.
    /// <para>A partitioned index returns one row per partition. They are folded into one row here —
    /// <c>MAX</c> of the fragmentation, since the worst partition is what makes a rebuild worth running, and
    /// <c>SUM</c> of the pages, since that is the index's size. Per-partition maintenance is a different
    /// action than the one this dialog is confirming.</para>
    /// <para>The heap of a heap table (<c>index_id = 0</c>) is left out: it has no name and
    /// <c>ALTER INDEX</c> cannot address it, so listing it would offer a row no button here acts on.</para>
    /// </remarks>
    public static string FragmentationStats(ISqlDialect dialect, string? schema, string table, string? index)
    {
        // OBJECT_ID reads its argument as text, so the identifiers are bracket-quoted inside the literal —
        // the same reason ConstraintCheck does it.
        var qualified = string.IsNullOrEmpty(schema)
            ? dialect.QuoteIdentifier(table)
            : $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(table)}";

        var one = index is { Length: > 0 } ? $" AND i.name = {Literal(index)}" : string.Empty;

        return $"""
            SELECT i.name, i.type_desc,
                CAST(MAX(ps.avg_fragmentation_in_percent) AS decimal(5,2)),
                SUM(ps.page_count)
            FROM sys.dm_db_index_physical_stats(
                DB_ID(), OBJECT_ID({Literal(qualified)}), NULL, NULL, 'LIMITED') AS ps
            JOIN sys.indexes AS i
                ON i.object_id = ps.object_id AND i.index_id = ps.index_id
            WHERE ps.index_level = 0 AND i.name IS NOT NULL{one}
            GROUP BY i.name, i.type_desc
            ORDER BY MAX(ps.avg_fragmentation_in_percent) DESC, i.name
            """;
    }

    /// <summary>A name as a quoted string literal for OBJECT_ID and the catalog comparison — the same
    /// doubling the rest of this plugin uses, so a bracket in an object name cannot end a literal.</summary>
    private static string Literal(string value) => $"N'{value.Replace("'", "''")}'";
}
