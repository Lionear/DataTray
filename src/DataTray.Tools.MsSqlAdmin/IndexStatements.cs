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

    /// <summary>A name as a quoted string literal for OBJECT_ID and the catalog comparison — the same
    /// doubling the rest of this plugin uses, so a bracket in an object name cannot end a literal.</summary>
    private static string Literal(string value) => $"N'{value.Replace("'", "''")}'";
}
