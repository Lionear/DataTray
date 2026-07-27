using System.Globalization;
using System.Text;
using DataTray.Sdk.Query;

namespace DataTray.Sdk.Scripting;

/// <summary>
/// The handful of engine differences that matter when rendering a value as a SQL literal — boolean
/// spelling and the binary-literal syntax. Everything else (quoted strings with <c>''</c> escaping,
/// numeric verbatim, <c>NULL</c>) is common across the SQL engines we target. Pick with
/// <see cref="SqlValueLiteral.DialectFor"/> from a provider id, or default to <see cref="Generic"/>.
/// </summary>
public enum SqlLiteralDialect
{
    Generic,
    SqlServer,
    Postgres,
    MySql,
    Sqlite
}

/// <summary>
/// Renders a CLR value (as it comes back in <see cref="QueryResult.Rows"/>) as a literal for generated
/// SQL — the "script data as INSERT" / copy-table path. Deliberately not on <see cref="ISqlDialect"/>:
/// it's a pure host+plugin helper, not part of the provider contract, so a tool plugin can reuse it
/// without an API bump. Same-engine scripting is the intended use, so the dialect only tweaks the two
/// spots the engines actually disagree on.
/// </summary>
public static class SqlValueLiteral
{
    /// <summary>Map a provider id (<c>postgres</c>/<c>mysql</c>/<c>sqlserver</c>/<c>sqlite</c>) to its
    /// literal dialect; anything else falls back to <see cref="SqlLiteralDialect.Generic"/>.</summary>
    public static SqlLiteralDialect DialectFor(string? providerId) => providerId switch
    {
        "sqlserver" => SqlLiteralDialect.SqlServer,
        "postgres" => SqlLiteralDialect.Postgres,
        "mysql" => SqlLiteralDialect.MySql,
        "sqlite" => SqlLiteralDialect.Sqlite,
        _ => SqlLiteralDialect.Generic
    };

    public static string Format(object? value, SqlLiteralDialect dialect)
    {
        if (value is null or DBNull)
        {
            return "NULL";
        }

        switch (value)
        {
            case bool b:
                return dialect switch
                {
                    // SQL Server has no boolean literal — bit takes 1/0. SQLite likewise stores 0/1.
                    SqlLiteralDialect.SqlServer or SqlLiteralDialect.Sqlite => b ? "1" : "0",
                    _ => b ? "TRUE" : "FALSE"
                };

            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;

            case float f:
                return f.ToString("R", CultureInfo.InvariantCulture);
            case double d:
                return d.ToString("R", CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);

            case byte[] bytes:
                return FormatBytes(bytes, dialect);

            case DateTime dt:
                return Quote(dt.ToString(
                    dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture));
            case DateTimeOffset dto:
                return Quote(dto.ToString("yyyy-MM-dd HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
            case DateOnly d:
                return Quote(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case TimeOnly t:
                return Quote(t.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            case TimeSpan ts:
                return Quote(ts.ToString());

            case Guid g:
                return Quote(g.ToString());

            default:
                return Quote(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    private static string FormatBytes(byte[] bytes, SqlLiteralDialect dialect)
    {
        var hex = Convert.ToHexString(bytes); // uppercase, no separators
        return dialect switch
        {
            SqlLiteralDialect.SqlServer => "0x" + hex,
            SqlLiteralDialect.Postgres => "'\\x" + hex + "'",   // bytea hex (standard_conforming_strings)
            _ => "X'" + hex + "'"                                  // MySQL / SQLite / generic
        };
    }
}

/// <summary>
/// Builds <c>INSERT INTO … VALUES …</c> statements from a materialised result set — the shared body of
/// "script data as INSERT" (host) and copy-table (tool plugin). Columns are dialect-quoted; values go
/// through <see cref="SqlValueLiteral"/>. Read-only/computed columns are skipped so the script re-inserts
/// cleanly. One statement per row, so the output is portable and diff-friendly.
/// </summary>
public static class InsertScripter
{
    public static string Build(
        string qualifiedTable,
        IReadOnlyList<ResultColumn> columns,
        IReadOnlyList<object?[]> rows,
        ISqlDialect dialect,
        SqlLiteralDialect literalDialect)
    {
        // Keep the original column index so each row's values line up after read-only columns are dropped.
        var writable = columns
            .Select((c, i) => (Column: c, Index: i))
            .Where(x => !x.Column.IsReadOnly)
            .ToList();

        if (writable.Count == 0)
        {
            return "-- No insertable columns.";
        }

        var columnList = string.Join(", ", writable.Select(x => dialect.QuoteIdentifier(x.Column.BaseColumn ?? x.Column.Name)));

        if (rows.Count == 0)
        {
            return $"-- No rows to script.\n-- INSERT INTO {qualifiedTable} ({columnList}) VALUES (…);";
        }

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var values = string.Join(", ", writable.Select(x =>
                SqlValueLiteral.Format(x.Index < row.Length ? row[x.Index] : null, literalDialect)));
            sb.Append("INSERT INTO ").Append(qualifiedTable).Append(" (").Append(columnList)
              .Append(") VALUES (").Append(values).Append(");\n");
        }

        return sb.ToString();
    }
}
