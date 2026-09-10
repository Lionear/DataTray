using System.Globalization;
using System.Text.Json;

namespace DataTray.Providers.Prometheus;

/// <summary>
/// Turns the <c>data</c> object of a Prometheus query response into a grid result.
/// </summary>
/// <remarks>
/// Prometheus returns one of four result types. Both series types are flattened to the same
/// <c>&lt;labels…&gt;, timestamp, value</c> shape — one row per <em>sample</em>, not per series — because
/// that long format is what a grid can show and what a chart viewer can group by: pick the label
/// columns as the series key, timestamp as X, value as Y.
/// <list type="bullet">
/// <item><c>vector</c> — one sample per series (an instant query).</item>
/// <item><c>matrix</c> — many samples per series (a range selector such as <c>up[5m]</c>, or a subquery).</item>
/// <item><c>scalar</c>/<c>string</c> — a single sample, no labels.</item>
/// </list>
/// </remarks>
public static class PrometheusResult
{
    public static QueryResult Shape(JsonElement data, TimeSpan elapsed)
    {
        var resultType = data.TryGetProperty("resultType", out var t) ? t.GetString() : null;
        var result = data.GetProperty("result");

        if (resultType is "scalar" or "string")
        {
            var (time, raw) = ReadSample(result);
            object? value = resultType == "string" ? raw : ParseValue(raw);
            return new QueryResult
            {
                Columns =
                [
                    TimestampColumn,
                    new ResultColumn("value", resultType == "string" ? typeof(string) : typeof(double))
                    {
                        IsReadOnly = true,
                        AllowDbNull = true
                    }
                ],
                Rows = [[time, value]],
                Elapsed = elapsed
            };
        }

        var labels = CollectLabels(result);
        var rows = new List<object?[]>();
        foreach (var series in result.EnumerateArray())
        {
            var metric = series.GetProperty("metric");
            var head = new object?[labels.Count];
            for (var i = 0; i < labels.Count; i++)
            {
                head[i] = metric.TryGetProperty(labels[i], out var label) ? label.GetString() : null;
            }

            if (series.TryGetProperty("values", out var samples))
            {
                foreach (var sample in samples.EnumerateArray())
                {
                    rows.Add(Row(head, sample));
                }
            }
            else if (series.TryGetProperty("value", out var single))
            {
                rows.Add(Row(head, single));
            }
        }

        var columns = new List<ResultColumn>(labels.Count + 2);
        columns.AddRange(labels.Select(name => new ResultColumn(name, typeof(string))
        {
            IsReadOnly = true,
            AllowDbNull = true
        }));
        columns.Add(TimestampColumn);
        columns.Add(new ResultColumn("value", typeof(double)) { IsReadOnly = true, AllowDbNull = true });

        return new QueryResult { Columns = columns, Rows = rows, Elapsed = elapsed };
    }

    private static ResultColumn TimestampColumn =>
        new("timestamp", typeof(DateTime)) { IsReadOnly = true };

    /// <summary>Label names across all series, so sparse labels still get a column. <c>__name__</c> leads.</summary>
    private static List<string> CollectLabels(JsonElement result)
    {
        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var series in result.EnumerateArray())
        {
            foreach (var label in series.GetProperty("metric").EnumerateObject())
            {
                if (seen.Add(label.Name))
                {
                    labels.Add(label.Name);
                }
            }
        }

        labels.Sort(StringComparer.Ordinal);
        if (labels.Remove("__name__"))
        {
            labels.Insert(0, "__name__");
        }

        return labels;
    }

    private static object?[] Row(object?[] labels, JsonElement sample)
    {
        var (time, raw) = ReadSample(sample);
        var row = new object?[labels.Length + 2];
        labels.CopyTo(row, 0);
        row[^2] = time;
        row[^1] = ParseValue(raw);
        return row;
    }

    /// <summary>A sample is the pair <c>[&lt;unix seconds, fractional&gt;, "&lt;value&gt;"]</c>.</summary>
    private static (DateTime Time, string Raw) ReadSample(JsonElement sample) =>
        (DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(sample[0].GetDouble() * 1000)).UtcDateTime,
            sample[1].GetString() ?? string.Empty);

    // Values arrive as strings so that Inf/NaN survive JSON, which has no literal for them.
    private static double? ParseValue(string raw) => raw switch
    {
        "+Inf" => double.PositiveInfinity,
        "-Inf" => double.NegativeInfinity,
        _ => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null
    };
}
