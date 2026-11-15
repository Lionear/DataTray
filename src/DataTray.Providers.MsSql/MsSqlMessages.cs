using DataTray.Sdk.Query;

namespace DataTray.Providers.MsSql;

/// <summary>SqlClient's messages as timeline lines (SE-300). Takes the fields of a <c>SqlError</c> because that
/// type has no public constructor to test with.</summary>
internal static class MsSqlMessages
{
    // SQL Server's own split: severity 0–10 is informational and arrives through InfoMessage; 11 and up is an
    // error that SqlClient turns into a SqlException.
    private const byte HighestInfoSeverity = 10;

    public static QueryMessage From(int number, byte severity, byte state, int line, string text)
    {
        if (severity > HighestInfoSeverity)
        {
            return new QueryMessage(QueryMessageKind.Error, text)
            {
                Header = $"Msg {number}, Level {severity}, State {state}, Line {line}",
                Code = $"Msg {number}",
            };
        }

        // The server sends its warnings (8153 "Null value is eliminated…") at severity 0 like any PRINT; the
        // text is the only thing that marks them.
        var kind = text.StartsWith("Warning:", StringComparison.Ordinal) ? QueryMessageKind.Warning : QueryMessageKind.Info;
        return new QueryMessage(kind, text);
    }

    public static QueryMessage RowsAffected(int rows) =>
        new(QueryMessageKind.RowsAffected, rows == 1 ? "(1 row affected)" : $"({rows} rows affected)");
}
