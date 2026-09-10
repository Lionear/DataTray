using System.Data.Common;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataTray.Sdk;

namespace DataTray.Providers.Prometheus;

/// <summary>
/// Prometheus over its HTTP API, in PromQL. Read-only by nature: the API exposes no writes, so every
/// DDL/DML seam throws and the grid stays uneditable.
/// </summary>
/// <remarks>
/// Everything runs through the instant-query endpoint <c>/api/v1/query</c>. That is not a shortcut past
/// range data: a range selector (<c>up[1h]</c>) or a subquery (<c>rate(x[5m])[1h:1m]</c>) already returns a
/// matrix there, so time series come back without inventing a syntax for start/end/step that PromQL
/// itself does not have. <c>/api/v1/query_range</c> would only add a way to say the same thing.
/// </remarks>
public sealed class PrometheusProvider : IDbProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public string DisplayName => "Prometheus";

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(PrometheusProvider), "🔥");

    public ISqlDialect Dialect { get; } = new PrometheusDialect();

    public bool IsSqlBased => false;

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("url", "URL", ConnectionFieldType.Text, Required: true, Default: "http://localhost:9090"),
        new("username", "Username", ConnectionFieldType.Text),
        new("password", "Password", ConnectionFieldType.Password),
        new("token", "Bearer token", ConnectionFieldType.Password, Advanced: true)
    ];

    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values)
    {
        var builder = new DbConnectionStringBuilder { ["Url"] = Value(values, "url") ?? "http://localhost:9090" };
        if (Value(values, "username") is { } user) builder["Username"] = user;
        if (Value(values, "password") is { } password) builder["Password"] = password;
        if (Value(values, "token") is { } token) builder["Token"] = token;
        return builder.ConnectionString;
    }

    public IReadOnlyDictionary<string, string?>? ParseConnectionString(string connectionString)
    {
        DbConnectionStringBuilder builder;
        try
        {
            builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch
        {
            return null;
        }

        var result = new Dictionary<string, string?>();
        if (builder.TryGetValue("Url", out var url)) result["url"] = url?.ToString();
        if (builder.TryGetValue("Username", out var user)) result["username"] = user?.ToString();
        // Secrets are never echoed back into the form; the keys only say "one is set".
        if (builder.TryGetValue("Token", out _)) result["token"] = "";
        return result;
    }

    // Cheapest call every Prometheus-compatible endpoint has to answer: evaluate the constant 1.
    public async Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await QueryAsync(profile, "1", ct);
        return true;
    }

    public async Task<string?> GetServerVersionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        try
        {
            var data = await ApiAsync(profile, "api/v1/status/buildinfo", null, ct);
            return data.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch
        {
            // Thanos, Mimir, VictoriaMetrics and friends speak the query API without buildinfo.
            return null;
        }
    }

    public async Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        if (ancestors.Count > 0)
        {
            return ancestors[^1].Kind == DbNodeKind.Table
                ? await GetLabelNodesAsync(profile, ancestors[^1].Name, ct)
                : [];
        }

        var names = await ApiAsync(profile, "api/v1/label/__name__/values", null, ct);
        var metadata = await GetMetadataAsync(profile, ct);
        var nodes = new List<DbTreeNode>();
        foreach (var name in names.EnumerateArray())
        {
            var metric = name.GetString();
            if (metric is null) continue;
            metadata.TryGetValue(metric, out var meta);
            nodes.Add(new DbTreeNode
            {
                Kind = DbNodeKind.Table,
                Name = metric,
                HasChildren = true,
                Detail = meta.Type,
                Tooltip = meta.Help
            });
        }

        nodes.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return nodes;
    }

    /// <summary>A metric's labels, shown as its "columns" — the dimensions you can filter or group by.</summary>
    private async Task<IReadOnlyList<DbTreeNode>> GetLabelNodesAsync(
        ConnectionProfile profile,
        string metric,
        CancellationToken ct)
    {
        var labels = await ApiAsync(profile, $"api/v1/labels?match%5B%5D={Uri.EscapeDataString(metric)}", null, ct);
        return labels.EnumerateArray()
            .Select(label => label.GetString())
            .Where(name => name is not null and not "__name__")
            .Order(StringComparer.Ordinal)
            .Select(name => new DbTreeNode { Kind = DbNodeKind.Column, Name = name!, Detail = "label" })
            .ToList();
    }

    private async Task<Dictionary<string, (string? Type, string? Help)>> GetMetadataAsync(
        ConnectionProfile profile,
        CancellationToken ct)
    {
        var result = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        try
        {
            var metadata = await ApiAsync(profile, "api/v1/metadata", null, ct);
            foreach (var metric in metadata.EnumerateObject())
            {
                // One metric can be exposed by several targets; the first entry is enough for a hint.
                var first = metric.Value.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object) continue;
                result[metric.Name] = (
                    first.TryGetProperty("type", out var type) ? type.GetString() : null,
                    first.TryGetProperty("help", out var help) ? help.GetString() : null);
            }
        }
        catch
        {
            // Metadata is decoration on the tree, never a reason to fail expanding it.
        }

        return result;
    }

    // Prometheus has no databases; the host shows no database picker for an empty list.
    public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public async Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var data = await QueryAsync(profile, sql, ct);
        return PrometheusResult.Shape(data, stopwatch.Elapsed);
    }

    public async Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(
        ConnectionProfile profile,
        string sql,
        CancellationToken ct) =>
        [await ExecuteQueryAsync(profile, sql, ct)];

    public Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct) =>
        throw new NotSupportedException("Prometheus does not expose a query plan.");

    public string? BuildNodeQuery(
        NodeQueryKind kind,
        IReadOnlyList<DbNodeRef> nodePath,
        IReadOnlyList<ResultColumn>? columns,
        ConnectionProfile profile)
    {
        if (nodePath.Count == 0 || nodePath[^1].Kind != DbNodeKind.Table)
        {
            return null;
        }

        var metric = nodePath[^1].Name;
        return kind switch
        {
            // "All rows" of a metric is its current sample per series; there is no top-N to ask for.
            NodeQueryKind.SelectAll or NodeQueryKind.SelectTop => metric,
            NodeQueryKind.Count => $"count({metric})",
            _ => null
        };
    }

    public IReadOnlyList<CreateCapability> CreateCapabilities { get; } = [];

    public IReadOnlyList<string> ColumnTypes { get; } = [];

    private static NotSupportedException ReadOnly() =>
        new("Prometheus is read-only: its HTTP API accepts queries, not writes.");

    public SqlStatement BuildCreateStatement(CreateObjectSpec spec) => throw ReadOnly();

    public Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw ReadOnly();

    public Task<int> ExecuteBatchAsync(
        ConnectionProfile profile,
        IReadOnlyList<SqlStatement> statements,
        CancellationToken ct) => throw ReadOnly();

    private static Task<JsonElement> QueryAsync(ConnectionProfile profile, string promql, CancellationToken ct) =>
        ApiAsync(profile, "api/v1/query", [new KeyValuePair<string, string>("query", promql.Trim())], ct);

    /// <summary>
    /// One call against the API, returning the <c>data</c> member. POST when there is a form to send:
    /// a PromQL expression easily outgrows a URL.
    /// </summary>
    private static async Task<JsonElement> ApiAsync(
        ConnectionProfile profile,
        string path,
        IReadOnlyList<KeyValuePair<string, string>>? form,
        CancellationToken ct)
    {
        var settings = new DbConnectionStringBuilder { ConnectionString = profile.ConnectionString };
        var baseUrl = Setting(settings, "Url") ?? "http://localhost:9090";

        using var request = new HttpRequestMessage(
            form is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path));

        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        if (Setting(settings, "Token") is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else if (Setting(settings, "Username") is { } user)
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{user}:{Setting(settings, "Password") ?? string.Empty}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // A rejected query answers 4xx *with* the reason in the envelope, so parse before checking the code.
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidOperationException($"Prometheus returned a non-JSON response: {Excerpt(body)}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("status", out var status) && status.GetString() != "success")
            {
                var message = root.TryGetProperty("error", out var error) ? error.GetString() : null;
                throw new InvalidOperationException(message ?? $"Prometheus request failed: {response.StatusCode}.");
            }

            response.EnsureSuccessStatusCode();
            // Cloned: the element outlives the document it was parsed from.
            return root.GetProperty("data").Clone();
        }
    }

    private static string Excerpt(string body) =>
        body.Length <= 200 ? body : body[..200] + "…";

    private static string? Setting(DbConnectionStringBuilder settings, string key) =>
        settings.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text ? text : null;

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
