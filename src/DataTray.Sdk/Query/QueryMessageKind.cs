namespace DataTray.Sdk.Query;

/// <summary>What a <see cref="QueryMessage"/> is, so the host can mark and filter it without parsing text.</summary>
public enum QueryMessageKind
{
    /// <summary>An informational server message: SQL Server <c>PRINT</c>/<c>RAISERROR</c> with severity 0–10,
    /// a PostgreSQL <c>NOTICE</c>/<c>INFO</c>, a MySQL note.</summary>
    Info,

    /// <summary>A per-statement row count, shown as <c>(n rows affected)</c>.</summary>
    RowsAffected,

    /// <summary>A server warning: a PostgreSQL <c>WARNING</c>, a MySQL warning, a SQL Server message that the
    /// server itself labels as one.</summary>
    Warning,

    /// <summary>A server error (SQL Server severity 11 and up). Reported in the same timeline as the messages,
    /// but it still fails the run: the provider throws as it always did.</summary>
    Error,
}
