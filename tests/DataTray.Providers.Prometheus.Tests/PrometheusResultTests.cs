using System.Text.Json;
using DataTray.Sdk.Query;

namespace DataTray.Providers.Prometheus.Tests;

public class PrometheusResultTests
{
    private static QueryResult Shape(string json) =>
        PrometheusResult.Shape(JsonDocument.Parse(json).RootElement, TimeSpan.Zero);

    [Fact]
    public void VectorGivesOneRowPerSeriesWithNameFirst()
    {
        var result = Shape(
            """
            {
              "resultType": "vector",
              "result": [
                { "metric": { "__name__": "up", "job": "api", "instance": "a:80" }, "value": [1435781451.781, "1"] },
                { "metric": { "__name__": "up", "job": "api" }, "value": [1435781451.781, "0"] }
              ]
            }
            """);

        Assert.Equal(["__name__", "instance", "job", "timestamp", "value"], result.Columns.Select(c => c.Name));
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("a:80", result.Rows[0][1]);
        // A label absent from one series still gets its column, left null for that row.
        Assert.Null(result.Rows[1][1]);
        Assert.Equal(new DateTime(2015, 7, 1, 20, 10, 51, 781, DateTimeKind.Utc), result.Rows[0][3]);
        Assert.Equal(1d, result.Rows[0][4]);
        Assert.Equal(0d, result.Rows[1][4]);
    }

    [Fact]
    public void MatrixIsFlattenedToOneRowPerSample()
    {
        var result = Shape(
            """
            {
              "resultType": "matrix",
              "result": [
                { "metric": { "__name__": "up" }, "values": [[1435781430.781, "1"], [1435781445.781, "+Inf"]] }
              ]
            }
            """);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(double.PositiveInfinity, result.Rows[1][2]);
    }

    [Fact]
    public void ScalarIsASingleTimestampedValue()
    {
        var result = Shape("""{ "resultType": "scalar", "result": [1435781451.781, "2"] }""");

        Assert.Equal(["timestamp", "value"], result.Columns.Select(c => c.Name));
        Assert.Equal(2d, Assert.Single(result.Rows)[1]);
    }

    [Fact]
    public void UnparsableValueBecomesNullRatherThanThrowing()
    {
        var result = Shape(
            """{ "resultType": "vector", "result": [{ "metric": {}, "value": [1435781451.781, "?"] }] }""");

        Assert.Null(Assert.Single(result.Rows)[1]);
    }
}
