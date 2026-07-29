using DataTray.Sdk;

namespace DataTray.Providers.ClickHouse;

public sealed class ClickHouseDialect : ISqlDialect
{
    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET",
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "ANY", "ASOF", "ON", "USING",
        "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "ILIKE", "BETWEEN", "GLOBAL",
        "AS", "DISTINCT", "UNION", "ALL", "INSERT", "INTO", "VALUES", "UPDATE",
        "SET", "DELETE", "ALTER", "WITH", "CASE", "WHEN", "THEN", "ELSE", "END",
        "ASC", "DESC", "TRUE", "FALSE", "COUNT", "SUM", "AVG", "MIN", "MAX",
        // ClickHouse's own vocabulary — the clauses that make it columnar rather than row-oriented.
        "PREWHERE", "FINAL", "SAMPLE", "ARRAY", "LATERAL", "TOTALS", "ROLLUP", "CUBE",
        "ENGINE", "PARTITION", "PRIMARY", "KEY", "TTL", "SETTINGS", "MATERIALIZED", "VIEW",
        "DATABASE", "TABLE", "DICTIONARY", "CREATE", "DROP", "TRUNCATE", "ATTACH", "DETACH",
        "OPTIMIZE", "SYSTEM", "FORMAT", "EXPLAIN", "CLUSTER"
    };

    // A representative built-in catalogue offered by completion (SE-149 phase 2) — not exhaustive.
    // Leans on the aggregate/array/date functions a ClickHouse user actually reaches for daily.
    public IReadOnlyList<SqlFunction> Functions { get; } =
    [
        new("count", "count(* | expression)", "Number of rows / non-null values."),
        new("sum", "sum(expression)", "Total of the values."),
        new("avg", "avg(expression)", "Arithmetic mean."),
        new("min", "min(expression)", "Smallest value."),
        new("max", "max(expression)", "Largest value."),
        new("uniq", "uniq(expression [, ...])", "Approximate number of distinct values."),
        new("uniqExact", "uniqExact(expression [, ...])", "Exact number of distinct values."),
        new("quantile", "quantile(level)(expression)", "Approximate quantile."),
        new("topK", "topK(n)(expression)", "The n most frequent values."),
        new("argMax", "argMax(value, criterion)", "Value at the row with the largest criterion."),
        new("argMin", "argMin(value, criterion)", "Value at the row with the smallest criterion."),
        new("groupArray", "groupArray(expression)", "Collect group values into an array."),
        new("countIf", "countIf(condition)", "Rows matching the condition (any -If combinator)."),
        new("sumIf", "sumIf(expression, condition)", "Conditional total."),
        new("coalesce", "coalesce(value [, ...])", "First non-null argument."),
        new("ifNull", "ifNull(value, fallback)", "Fallback when the value is NULL."),
        new("nullIf", "nullIf(value1, value2)", "NULL when the two are equal, else value1."),
        new("greatest", "greatest(value [, ...])", "Largest of the arguments."),
        new("least", "least(value [, ...])", "Smallest of the arguments."),
        new("lower", "lower(string)", "Lower-case the string."),
        new("upper", "upper(string)", "Upper-case the string."),
        new("length", "length(string | array)", "Number of characters / elements."),
        new("trim", "trim(string)", "Strip leading/trailing spaces."),
        new("substring", "substring(string, offset [, length])", "Extract a substring."),
        new("concat", "concat(value [, ...])", "Concatenate the arguments."),
        new("replaceAll", "replaceAll(haystack, pattern, replacement)", "Replace all occurrences."),
        new("splitByChar", "splitByChar(separator, string)", "Split into an array."),
        new("arrayJoin", "arrayJoin(array)", "Expand an array into one row per element."),
        new("has", "has(array, element)", "True when the array contains the element."),
        new("round", "round(number [, decimals])", "Round to the given precision."),
        new("abs", "abs(number)", "Absolute value."),
        new("now", "now()", "Current date and time."),
        new("today", "today()", "Current date."),
        new("toDate", "toDate(value)", "Convert to a Date."),
        new("toDateTime", "toDateTime(value)", "Convert to a DateTime."),
        new("toStartOfMonth", "toStartOfMonth(date)", "Truncate to the first of the month."),
        new("toStartOfInterval", "toStartOfInterval(date, INTERVAL n unit)", "Bucket a timestamp."),
        new("dateDiff", "dateDiff(unit, start, end)", "Difference between two dates."),
        new("formatDateTime", "formatDateTime(time, format)", "Format a timestamp as text."),
        new("cast", "cast(expression AS type)", "Convert to another type."),
        new("version", "version()", "The server's version string.")
    ];

    // ClickHouse quotes identifiers with backticks (double quotes also work); an embedded backtick is
    // escaped by doubling it.
    public string QuoteIdentifier(string identifier) =>
        $"`{identifier.Replace("`", "``")}`";

    // ClickHouse has no schema layer between database and table — a database IS the namespace, so a
    // qualified name is two-part `db`.`table`, exactly like MySQL. Naming the database explicitly also
    // makes generated SQL resolve from a query tab with no database context.
    public string QualifyName(string? database, string? schema, string table) =>
        string.IsNullOrEmpty(database)
            ? QuoteIdentifier(table)
            : $"{QuoteIdentifier(database)}.{QuoteIdentifier(table)}";

    public string Paginate(string sql, int limit, int offset, string? orderBy = null)
    {
        var order = orderBy is null ? string.Empty : $"\nORDER BY {orderBy}";
        return $"{sql}{order}\nLIMIT {limit} OFFSET {offset}";
    }
}
