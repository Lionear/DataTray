using DataTray.Sdk.Query;
using Npgsql;

namespace DataTray.Providers.Postgres;

/// <summary>Npgsql's notices and per-statement row counts as timeline lines (SE-300).</summary>
internal static class PostgresMessages
{
    public static QueryMessage FromNotice(string invariantSeverity, string text) =>
        new(invariantSeverity == "WARNING" ? QueryMessageKind.Warning : QueryMessageKind.Info, text);

    /// <summary>A row count for an INSERT/UPDATE/DELETE/MERGE/COPY; null for the rest — a SELECT's rows are in
    /// its grid, and DDL has no count.</summary>
    public static QueryMessage? FromStatement(StatementType type, int rows) =>
        type is StatementType.Insert or StatementType.Update or StatementType.Delete or StatementType.Merge or StatementType.Copy
            ? new QueryMessage(QueryMessageKind.RowsAffected, rows == 1 ? "(1 row affected)" : $"({rows} rows affected)")
            : null;
}
