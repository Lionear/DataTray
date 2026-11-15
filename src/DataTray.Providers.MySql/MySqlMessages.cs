using DataTray.Sdk.Query;

namespace DataTray.Providers.MySql;

/// <summary>MySqlConnector's warnings as timeline lines (SE-300). Level is the <c>SHOW WARNINGS</c> level:
/// Note, Warning or Error.</summary>
internal static class MySqlMessages
{
    public static QueryMessage From(string level, int code, string text) =>
        new(level switch
        {
            "Warning" => QueryMessageKind.Warning,
            "Error" => QueryMessageKind.Error,
            _ => QueryMessageKind.Info,
        }, text)
        {
            Code = code.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
}
