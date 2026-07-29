using DataTray.Sdk;

namespace DataTray.Providers.DuckDb;

public sealed class DuckDbDialect : ISqlDialect
{
    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET",
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "NATURAL", "ON", "USING",
        "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "ILIKE", "SIMILAR", "BETWEEN",
        "AS", "DISTINCT", "UNION", "INTERSECT", "EXCEPT", "ALL", "INSERT", "INTO", "VALUES",
        "UPDATE", "SET", "DELETE", "WITH", "RECURSIVE", "CASE", "WHEN", "THEN", "ELSE", "END",
        "ASC", "DESC", "NULLS", "FIRST", "LAST", "TRUE", "FALSE", "CAST", "TRY_CAST",
        "CREATE", "REPLACE", "TABLE", "VIEW", "SCHEMA", "SEQUENCE", "INDEX", "MACRO", "TYPE",
        "DROP", "ALTER", "TRUNCATE", "ATTACH", "DETACH", "PRIMARY", "KEY", "UNIQUE", "CHECK",
        "REFERENCES", "DEFAULT", "CONSTRAINT", "TEMPORARY", "TEMP", "IF", "EXISTS",
        "OVER", "PARTITION", "WINDOW", "ROWS", "RANGE", "PRECEDING", "FOLLOWING", "CURRENT", "ROW",
        "COUNT", "SUM", "AVG", "MIN", "MAX", "EXPLAIN", "ANALYZE", "PRAGMA", "COPY", "TO",
        // DuckDB's own analytics vocabulary — the parts that are not Postgres.
        "QUALIFY", "EXCLUDE", "COLUMNS", "PIVOT", "UNPIVOT", "ASOF", "POSITIONAL", "SEMI", "ANTI",
        "SUMMARIZE", "DESCRIBE", "INSTALL", "LOAD", "FORMAT", "PARQUET", "STRUCT", "MAP", "LIST"
    };

    // A representative built-in catalogue offered by completion (SE-149 phase 2) — not exhaustive.
    // Weighted towards what makes DuckDB worth reaching for: file readers, list/struct handling, stats.
    public IReadOnlyList<SqlFunction> Functions { get; } =
    [
        new("count", "count(* | expression)", "Number of rows / non-null values."),
        new("sum", "sum(expression)", "Total of the values."),
        new("avg", "avg(expression)", "Arithmetic mean."),
        new("min", "min(expression)", "Smallest value."),
        new("max", "max(expression)", "Largest value."),
        new("median", "median(expression)", "Middle value."),
        new("quantile_cont", "quantile_cont(expression, fraction)", "Interpolated quantile."),
        new("stddev", "stddev(expression)", "Sample standard deviation."),
        new("corr", "corr(y, x)", "Correlation coefficient."),
        new("mode", "mode(expression)", "Most frequent value."),
        new("arg_max", "arg_max(value, criterion)", "Value at the row with the largest criterion."),
        new("arg_min", "arg_min(value, criterion)", "Value at the row with the smallest criterion."),
        new("first", "first(expression)", "First value in the group."),
        new("last", "last(expression)", "Last value in the group."),
        new("string_agg", "string_agg(expression, separator)", "Concatenate group values."),
        new("coalesce", "coalesce(value [, ...])", "First non-null argument."),
        new("ifnull", "ifnull(value, fallback)", "Fallback when the value is NULL."),
        new("nullif", "nullif(value1, value2)", "NULL when the two are equal, else value1."),
        new("greatest", "greatest(value [, ...])", "Largest of the arguments."),
        new("least", "least(value [, ...])", "Smallest of the arguments."),
        new("lower", "lower(string)", "Lower-case the string."),
        new("upper", "upper(string)", "Upper-case the string."),
        new("length", "length(string | list)", "Number of characters / elements."),
        new("trim", "trim(string)", "Strip leading/trailing spaces."),
        new("substring", "substring(string, start [, length])", "Extract a substring."),
        new("concat", "concat(value [, ...])", "Concatenate the arguments."),
        new("regexp_matches", "regexp_matches(string, pattern)", "True when the pattern matches."),
        new("regexp_replace", "regexp_replace(string, pattern, replacement)", "Replace by regex."),
        new("split_part", "split_part(string, separator, index)", "One part of a split string."),
        new("list_transform", "list_transform(list, lambda)", "Map a lambda over a list."),
        new("list_filter", "list_filter(list, lambda)", "Keep the elements matching a lambda."),
        new("unnest", "unnest(list | struct)", "Expand into one row per element."),
        new("struct_pack", "struct_pack(name := value [, ...])", "Build a STRUCT value."),
        new("round", "round(number [, decimals])", "Round to the given precision."),
        new("abs", "abs(number)", "Absolute value."),
        new("now", "now()", "Current timestamp with time zone."),
        new("today", "today()", "Current date."),
        new("date_trunc", "date_trunc(part, timestamp)", "Truncate to a date part."),
        new("date_diff", "date_diff(part, start, end)", "Difference between two timestamps."),
        new("strftime", "strftime(timestamp, format)", "Format a timestamp as text."),
        new("strptime", "strptime(string, format)", "Parse text into a timestamp."),
        // The reason to reach for DuckDB in the first place: query a file as if it were a table.
        new("read_parquet", "read_parquet('path' [, ...])", "Read one or more Parquet files as a table."),
        new("read_csv", "read_csv('path' [, ...])", "Read CSV with automatic type detection."),
        new("read_csv_auto", "read_csv_auto('path')", "Read CSV, inferring the schema."),
        new("read_json", "read_json('path' [, ...])", "Read newline-delimited or array JSON."),
        new("read_json_auto", "read_json_auto('path')", "Read JSON, inferring the schema."),
        new("glob", "glob('pattern')", "List the files matching a pattern."),
        new("range", "range(start, stop [, step])", "Generate a range of numbers as rows."),
        new("generate_series", "generate_series(start, stop [, step])", "Inclusive numeric/date series."),
        new("version", "version()", "The DuckDB library version.")
    ];

    // DuckDB follows Postgres/ANSI: double-quoted identifiers, an embedded quote doubled.
    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    // DuckDB addresses objects as catalog.schema.table, but one connection is one database file, so the
    // catalog is always implicit here (as it is for a Postgres connection) and generated SQL stays
    // schema-qualified. The `database` argument is accepted and ignored for exactly that reason.
    public string QualifyName(string? database, string? schema, string table) =>
        string.IsNullOrEmpty(schema)
            ? QuoteIdentifier(table)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

    public string Paginate(string sql, int limit, int offset, string? orderBy = null)
    {
        var order = orderBy is null ? string.Empty : $"\nORDER BY {orderBy}";
        return $"{sql}{order}\nLIMIT {limit} OFFSET {offset}";
    }
}
