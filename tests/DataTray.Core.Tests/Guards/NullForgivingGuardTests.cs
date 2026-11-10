namespace DataTray.Core.Tests.Guards;

// Owner decision (SE-299): the null-forgiving operator is not allowed in DataTray. Older files are frozen by a baseline.
public class NullForgivingGuardTests
{
    private static readonly string RepoRoot = NullForgivingBaseline.FindRepoRoot();

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<int>>> Scan = new(() => NullForgivingScanner.ScanRepository(RepoRoot));

    private static readonly Lazy<IReadOnlyDictionary<string, int>> Baseline = new(() => NullForgivingBaseline.Load(RepoRoot));

    [Theory]
    [InlineData("class C { string M(string? s) => s!; }", new[] { 1 })]
    [InlineData("class C\n{\n    int M(int[]? a) => a![0] + a!.Length;\n}", new[] { 3, 3 })]
    [InlineData("class C { bool M(int a, bool b) => a != 1 && !b; }", new int[0])]
    [InlineData("class C { string M() => \"x!\"; } // y!", new int[0])]
    public void Scanner_FindsOnlyTheNullForgivingOperator(string source, int[] expectedLines)
    {
        Assert.Equal(expectedLines, NullForgivingScanner.Lines(source));
    }

    [Fact]
    public void Excess_SaysHowManyAreNewAndListsEveryLine()
    {
        var scan = new Dictionary<string, IReadOnlyList<int>> { ["src/A.cs"] = [3, 7, 9] };
        var baseline = new Dictionary<string, int> { ["src/A.cs"] = 1 };

        var excess = NullForgivingBaseline.Excess(scan, baseline);

        Assert.Equal(
            [
                "src/A.cs:3: the null-forgiving operator is not allowed in DataTray (2 more than the baseline of 1 in this file)",
                "src/A.cs:7: the null-forgiving operator is not allowed in DataTray (2 more than the baseline of 1 in this file)",
                "src/A.cs:9: the null-forgiving operator is not allowed in DataTray (2 more than the baseline of 1 in this file)",
            ],
            excess);
    }

    [Fact]
    public void Repository_HasNoNullForgivingOperatorBeyondTheBaseline()
    {
        var excess = NullForgivingBaseline.Excess(Scan.Value, Baseline.Value);

        Assert.True(excess.Count == 0, string.Join(Environment.NewLine, excess));
    }

    [Fact]
    public void Baseline_IsLoweredWhenAFileIsCleanedUp()
    {
        var stale = NullForgivingBaseline.Stale(Scan.Value, Baseline.Value);

        Assert.True(stale.Count == 0, string.Join(Environment.NewLine, stale));
    }

    [Theory]
    [InlineData("plugins/Extensions.QueryBuilder/")]
    [InlineData("tests/DataTray.Extensions.QueryBuilder.Tests/")]
    public void Baseline_ExemptsNothingInTheQueryBuilder(string folder)
    {
        Assert.DoesNotContain(Baseline.Value.Keys, file => file.StartsWith(folder, StringComparison.Ordinal));
    }
}
