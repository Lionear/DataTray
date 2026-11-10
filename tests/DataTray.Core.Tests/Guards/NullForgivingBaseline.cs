using System.Text.Json;

namespace DataTray.Core.Tests.Guards;

// The committed counts of null-forgiving operators that predate the owner decision, compared against a scan.
internal static class NullForgivingBaseline
{
    public const string RelativePath = "tests/DataTray.Core.Tests/Guards/null-forgiving-baseline.json";

    /// <summary>The repository root: the nearest folder above the test output that holds DataTray.slnx.</summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DataTray.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("DataTray.slnx not found above the test output.");
    }

    public static IReadOnlyDictionary<string, int> Load(string repoRoot) =>
        JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(repoRoot, RelativePath)))
        ?? throw new InvalidOperationException($"{RelativePath} is empty.");

    /// <summary>One message per occurrence in a file that has more than its baseline (a file not listed has zero).</summary>
    public static IReadOnlyList<string> Excess(IReadOnlyDictionary<string, IReadOnlyList<int>> scan, IReadOnlyDictionary<string, int> baseline) =>
        scan
            .Where(file => file.Value.Count > baseline.GetValueOrDefault(file.Key))
            .SelectMany(file => file.Value.Select(line =>
                $"{file.Key}:{line}: the null-forgiving operator is not allowed in DataTray " +
                $"({file.Value.Count - baseline.GetValueOrDefault(file.Key)} more than the baseline of {baseline.GetValueOrDefault(file.Key)} in this file)"))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Files that now have fewer than their baseline, so the cleanup is recorded (a ratchet).</summary>
    public static IReadOnlyList<string> Stale(IReadOnlyDictionary<string, IReadOnlyList<int>> scan, IReadOnlyDictionary<string, int> baseline) =>
        baseline
            .Select(entry => (File: entry.Key, Allowed: entry.Value, Found: scan.GetValueOrDefault(entry.Key)?.Count ?? 0))
            .Where(entry => entry.Found < entry.Allowed)
            .Select(entry => $"lower the baseline for {entry.File} to {entry.Found} in {RelativePath}")
            .Order(StringComparer.Ordinal)
            .ToList();
}
