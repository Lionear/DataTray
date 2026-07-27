using DataTray.Sdk;

namespace DataTray.Core.Ddl;

/// <summary>
/// Host-only DROP/ALTER SQL building — deliberately NOT part of the provider SDK (unlike DDL Create's
/// <c>BuildCreateStatement</c>). The syntax needed here is close enough across engines that
/// <see cref="ISqlDialect.QuoteIdentifier"/> plus a plain template is enough: <c>ADD</c> without the
/// optional <c>COLUMN</c> keyword and <c>DROP COLUMN</c> both work unchanged on Postgres/MsSql/MySql/
/// SQLite (verified live against all four). Runs via the existing <c>IDbProvider.ExecuteDdlAsync</c> —
/// no SDK member, no host-API bump.
///
/// One deliberate exception: SQL Server has no ALTER-based column rename at all — renaming is the
/// <c>sp_rename</c> stored procedure, a categorically different statement shape, not just different
/// keywords — so <see cref="RenameColumn"/> is the one place here that branches on the provider id.
/// </summary>
public static class AlterStatementBuilder
{
    public static string DropDatabase(ISqlDialect dialect, string name) =>
        $"DROP DATABASE {dialect.QuoteIdentifier(name)}";

    public static string DropSchema(ISqlDialect dialect, string name) =>
        $"DROP SCHEMA {dialect.QuoteIdentifier(name)}";

    public static string DropTable(ISqlDialect dialect, string? schema, string table, bool isView) =>
        $"DROP {(isView ? "VIEW" : "TABLE")} {Qualify(dialect, schema, table)}";

    // SQLite has no TRUNCATE — an unqualified DELETE is its optimised equivalent; the rest share TRUNCATE.
    public static string Truncate(string providerId, ISqlDialect dialect, string? schema, string table) =>
        providerId == "sqlite"
            ? $"DELETE FROM {Qualify(dialect, schema, table)}"
            : $"TRUNCATE TABLE {Qualify(dialect, schema, table)}";

    public static string AddColumn(ISqlDialect dialect, string? schema, string table, string column, string type, bool nullable) =>
        $"ALTER TABLE {Qualify(dialect, schema, table)} ADD {dialect.QuoteIdentifier(column)} {type}{(nullable ? "" : " NOT NULL")}";

    public static string DropColumn(ISqlDialect dialect, string? schema, string table, string column) =>
        $"ALTER TABLE {Qualify(dialect, schema, table)} DROP COLUMN {dialect.QuoteIdentifier(column)}";

    public static string RenameColumn(string providerId, ISqlDialect dialect, string? schema, string table, string oldName, string newName) =>
        providerId == "sqlserver"
            ? $"EXEC sp_rename '{EscapeLiteral(Qualified(schema, table))}.{EscapeLiteral(oldName)}', '{EscapeLiteral(newName)}', 'COLUMN'"
            : $"ALTER TABLE {Qualify(dialect, schema, table)} RENAME COLUMN {dialect.QuoteIdentifier(oldName)} TO {dialect.QuoteIdentifier(newName)}";

    private static string Qualify(ISqlDialect dialect, string? schema, string table) =>
        schema is { Length: > 0 }
            ? $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(table)}"
            : dialect.QuoteIdentifier(table);

    private static string Qualified(string? schema, string table) =>
        schema is { Length: > 0 } ? $"{schema}.{table}" : table;

    private static string EscapeLiteral(string value) => value.Replace("'", "''");
}
