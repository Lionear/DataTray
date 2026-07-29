using System.Text;
using System.Text.RegularExpressions;

namespace DataTray.Providers.DuckDb;

/// <summary>
/// The single table a result set traces back to, recovered from the query's text so the editable grid can
/// work at all.
/// </summary>
/// <remarks>
/// Every other SQL provider here gets this for free: the driver reports each result column's base table and
/// primary-key flag (MySqlConnector from the wire protocol, Microsoft.Data.Sqlite from a
/// <c>SQLITE_ENABLE_COLUMN_METADATA</c> build), and the host's editability test reads
/// <c>ResultColumn.BaseTable</c> + <c>IsKey</c> straight off that. DuckDB.NET reports neither — its reader
/// does not implement <c>IDbColumnSchemaGenerator</c>, and its <c>GetSchemaTable()</c> carries no
/// <c>BaseTableName</c> column at all — so without recovering the table some other way every DuckDB grid
/// would be read-only, including the plain double-click browse the rest of the app makes editable.
///
/// <para><see cref="From"/> is deliberately narrow: <c>SELECT &lt;names&gt; FROM &lt;name&gt;</c> with
/// nothing that could make a row ambiguous. Anything else — a join, a set operation, aggregation, a
/// subquery, or a table function such as <c>read_parquet('…')</c> — yields null and the grid stays
/// read-only. That asymmetry is the safe one: a missed match costs an edit the user can still make in SQL,
/// while a false match would let the host generate an <c>UPDATE … WHERE key = …</c> against the wrong
/// table.</para>
/// </remarks>
/// <param name="Schema">The schema, or null when the query named the table unqualified.</param>
public sealed record DuckDbBrowseTarget(string? Schema, string Table)
{
    // A quoted "name" (with "" escapes) or a bare identifier, optionally schema-qualified.
    private const string Identifier = """(?:"(?:[^"]|"")+"|[A-Za-z_][A-Za-z0-9_$]*)""";

    // Anchored at both ends, so nothing may follow the table name except the recognised trailing clauses.
    private static readonly Regex SingleTableSelect = new(
        $"""
        ^\s*SELECT\s+(?<columns>.+?)\s+FROM\s+
        (?:(?<schema>{Identifier})\s*\.\s*)?(?<table>{Identifier})
        \s*(?<rest>(?:WHERE|ORDER\s+BY|LIMIT|OFFSET)\b.*)?$
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace,
        TimeSpan.FromMilliseconds(200));

    // Anything in the column list or the trailing clauses that means a row no longer maps 1:1 onto a stored
    // row. `(` covers every subquery and function call, including the table functions that make DuckDB
    // interesting (read_parquet, read_csv, range) — none of which have a table to write back to.
    private static readonly Regex Disqualifying = new(
        @"\b(JOIN|UNION|INTERSECT|EXCEPT|GROUP\s+BY|HAVING|DISTINCT|QUALIFY|PIVOT|UNPIVOT|USING|WINDOW|OVER)\b|\(",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));

    /// <summary>The table this query reads, or null when the text is anything less simple than one
    /// table's rows.</summary>
    public static DuckDbBrowseTarget? From(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        var text = StripComments(sql).Trim().TrimEnd(';').Trim();

        Match match;
        try
        {
            match = SingleTableSelect.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological text is not worth an editable grid; fall back to read-only.
            return null;
        }

        if (!match.Success
            || Disqualifying.IsMatch(match.Groups["columns"].Value)
            || Disqualifying.IsMatch(match.Groups["rest"].Value))
        {
            return null;
        }

        return new DuckDbBrowseTarget(
            Unquote(match.Groups["schema"].Success ? match.Groups["schema"].Value : null),
            Unquote(match.Groups["table"].Value)!);
    }

    // Comments go first so `-- FROM other_table` can neither create nor break a match. String literals are
    // copied verbatim, so a `--` or `/*` inside one is not mistaken for a comment.
    private static string StripComments(string sql)
    {
        var result = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            if (sql[i] == '\'')
            {
                var start = i++;
                while (i < sql.Length && sql[i] != '\'')
                {
                    i += sql[i] == '\\' ? 2 : 1;
                }

                i = Math.Min(i + 1, sql.Length);
                result.Append(sql.AsSpan(start, i - start));
                continue;
            }

            if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                result.Append(' ');
                continue;
            }

            if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 2, sql.Length);
                result.Append(' ');
                continue;
            }

            result.Append(sql[i++]);
        }

        return result.ToString();
    }

    private static string? Unquote(string? identifier) =>
        identifier is null
            ? null
            : identifier.Length >= 2 && identifier.StartsWith('"') && identifier.EndsWith('"')
                ? identifier[1..^1].Replace("\"\"", "\"")
                : identifier;
}
