using DataTray.Sdk;

namespace DataTray.Providers.Prometheus;

/// <summary>
/// Prometheus has no SQL dialect — this exists only to satisfy the <see cref="IDbProvider.Dialect"/>
/// contract. The query language is PromQL, so identifiers are never quoted (a metric name is
/// <c>[a-zA-Z_:][a-zA-Z0-9_:]*</c> by definition) and there is no database/schema qualification.
/// </summary>
public sealed class PrometheusDialect : ISqlDialect
{
    // Fed to the console for highlighting/completion; PromQL's operator keywords, not SQL's.
    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "by", "without", "on", "ignoring", "group_left", "group_right", "offset", "bool",
        "and", "or", "unless", "start", "end"
    };

    public string QuoteIdentifier(string identifier) => identifier;

    public string QualifyName(string? database, string? schema, string table) => table;

    // The HTTP API has no LIMIT/OFFSET: an instant query returns one sample per matching series and
    // that is the whole result. Appending anything here would only produce invalid PromQL, so paging
    // is a no-op — every page is the same result.
    public string Paginate(string sql, int limit, int offset, string? orderBy = null) => sql;
}
