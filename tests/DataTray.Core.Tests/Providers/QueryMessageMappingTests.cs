using DataTray.Providers.MsSql;
using DataTray.Providers.MySql;
using DataTray.Providers.Postgres;
using DataTray.Sdk.Query;
using Npgsql;

namespace DataTray.Core.Tests.Providers;

// SE-300: how each driver's messages become timeline lines. The drivers' own event-args types cannot be built
// outside the drivers, so the mapping takes their fields; that the events arrive live was checked against real
// servers (SQL Server 2025, PostgreSQL 18, MariaDB 11).
public class QueryMessageMappingTests
{
    public static IEnumerable<object?[]> SqlServerMessages() =>
    [
        // number, class, state, line, text → kind, header, code
        [0, 0, 1, 2, "Purging (3 regions)", QueryMessageKind.Info, null, null],                          // PRINT
        [50000, 10, 1, 9, "Region 0  deleted total: 1000", QueryMessageKind.Info, null, null],           // RAISERROR sev 10
        [8153, 0, 1, 15, "Warning: Null value is eliminated by an aggregate or other SET operation.", QueryMessageKind.Warning, null, null],
        [547, 16, 0, 4, "The DELETE statement conflicted", QueryMessageKind.Error, "Msg 547, Level 16, State 0, Line 4", "Msg 547"],
        [50000, 11, 1, 1, "custom fail", QueryMessageKind.Error, "Msg 50000, Level 11, State 1, Line 1", "Msg 50000"],
    ];

    [Theory, MemberData(nameof(SqlServerMessages))]
    public void SqlServer_SeverityElevenAndUpIsAnErrorWithTheServerHeader(
        int number, int severity, int state, int line, string text, QueryMessageKind kind, string? header, string? code)
    {
        var message = MsSqlMessages.From(number, (byte)severity, (byte)state, line, text);

        Assert.Equal(new QueryMessage(kind, text) { Header = header, Code = code }, message);
    }

    [Theory]
    [InlineData(1, "(1 row affected)")]
    [InlineData(20000, "(20000 rows affected)")]
    [InlineData(0, "(0 rows affected)")]
    public void RowsAffected_ReadsLikeSsms(int rows, string text) =>
        Assert.Equal(new QueryMessage(QueryMessageKind.RowsAffected, text), MsSqlMessages.RowsAffected(rows));

    [Theory]
    [InlineData("NOTICE", QueryMessageKind.Info)]
    [InlineData("INFO", QueryMessageKind.Info)]
    [InlineData("LOG", QueryMessageKind.Info)]
    [InlineData("WARNING", QueryMessageKind.Warning)]
    public void Postgres_NoticeSeverityPicksTheKind(string severity, QueryMessageKind kind) =>
        Assert.Equal(new QueryMessage(kind, "tick 1"), PostgresMessages.FromNotice(severity, "tick 1"));

    public static IEnumerable<object[]> PostgresDataChanges() =>
    [
        [StatementType.Insert, 5, "(5 rows affected)"],
        [StatementType.Update, 1, "(1 row affected)"],
        [StatementType.Delete, 0, "(0 rows affected)"],
        [StatementType.Merge, 2, "(2 rows affected)"],
        [StatementType.Copy, 3, "(3 rows affected)"],
    ];

    [Theory, MemberData(nameof(PostgresDataChanges))]
    public void Postgres_DataChangesGetARowCount(object type, int rows, string text) =>
        Assert.Equal(new QueryMessage(QueryMessageKind.RowsAffected, text), PostgresMessages.FromStatement((StatementType)type, rows));

    public static IEnumerable<object[]> PostgresOtherStatements() =>
    [
        [StatementType.Select, 5],    // the grid already shows its rows
        [StatementType.Other, -1],    // DDL, DO blocks
    ];

    [Theory, MemberData(nameof(PostgresOtherStatements))]
    public void Postgres_OtherStatementsGetNoRowCount(object type, int rows) =>
        Assert.Null(PostgresMessages.FromStatement((StatementType)type, rows));

    [Theory]
    [InlineData("Note", QueryMessageKind.Info)]
    [InlineData("Warning", QueryMessageKind.Warning)]
    [InlineData("Error", QueryMessageKind.Error)]
    public void MySql_WarningLevelPicksTheKind(string level, QueryMessageKind kind) =>
        Assert.Equal(new QueryMessage(kind, "Data truncated for column 's' at row 1") { Code = "1265" },
            MySqlMessages.From(level, 1265, "Data truncated for column 's' at row 1"));
}
