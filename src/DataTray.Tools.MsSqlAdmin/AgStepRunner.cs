using System.Text;
using System.Text.RegularExpressions;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// Runs an Always On plan — the failover's or the new-group wizard's — step by step, each on the instance it
/// names, reporting every step as a checklist row so a plan that stops halfway shows exactly how far it got.
/// </summary>
internal static partial class AgStepRunner
{
    /// <summary>How long a wait step polls before giving up.</summary>
    internal static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(2);

    /// <summary>How long a <see cref="AgStep.Retries"/> step keeps being retried.</summary>
    internal static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(30);

    public static async Task RunAsync(
        IReadOnlyList<AgStep> steps,
        Func<string, ReplicaConnection?> connectionFor,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        // Every connection in hand before the first statement, so no plan stops halfway for want of one.
        var runners = steps.Select(s => s.Replica).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(r => r, r => connectionFor(r)
                ?? throw new InvalidOperationException($"No verified connection to {r}. Nothing was run."), StringComparer.OrdinalIgnoreCase);
        var captured = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var key = $"step{i}";
            var label = $"{i + 1}. {step.Replica}: {step.Purpose}";
            progress.Report(new ToolProgress(label, (double)i / steps.Count, key, ToolItemStatus.Running));

            var runner = runners[step.Replica];
            var sql = Substitute(step.Sql, captured);
            try
            {
                if (step.IsWait)
                {
                    await WaitForZeroAsync(runner, sql, ct);
                }
                else if (step.CaptureAs is { } name)
                {
                    var result = await runner.QueryAsync(sql, ct);
                    captured[name] = Literal(result.Rows.FirstOrDefault()?[0])
                        ?? throw new InvalidOperationException($"{step.Purpose} returned nothing on {step.Replica}.");
                }
                else if (step.Retries)
                {
                    await RetryAsync(() => runner.Provider.ExecuteDdlAsync(runner.Profile, sql, ct), ct);
                }
                else
                {
                    await runner.Provider.ExecuteDdlAsync(runner.Profile, sql, ct);
                }
            }
            catch
            {
                progress.Report(new ToolProgress(label, null, key, ToolItemStatus.Error));
                throw;
            }

            progress.Report(new ToolProgress(label, (double)(i + 1) / steps.Count, key, ToolItemStatus.Done));
        }
    }

    /// <summary>The steps as the script the user reviews — also what a tool's <c>ExecuteAsync</c> compares
    /// against the plan it rebuilds from fresh state, so a plan that changed since review is refused, not run.
    /// Captured values stay as their <c>$(…)</c> tokens: they do not exist until the run reads them.</summary>
    public static string Script(IReadOnlyList<AgStep> steps)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            sb.Append("-- ").Append(i + 1).Append(". on ").Append(step.Replica).Append(": ").AppendLine(step.Purpose);
            sb.AppendLine(step.Sql.TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Replace every <c>$(cert:NAME)</c> with the value captured under it — only that prefix, so an object
    /// that merely has <c>$(</c> in its name is left alone. A token nothing captured is an
    /// error, not an empty string: an unfilled certificate must never reach the server as SQL.</summary>
    internal static string Substitute(string sql, IReadOnlyDictionary<string, string> captured) =>
        Token().Replace(sql, m => captured.TryGetValue(m.Groups[1].Value, out var v)
            ? v
            : throw new InvalidOperationException($"Nothing was captured for $({m.Groups[1].Value})."));

    /// <summary>A captured value as a T-SQL literal: binary as <c>0x…</c> (a certificate), anything else refused.</summary>
    internal static string? Literal(object? value) => value switch
    {
        byte[] { Length: > 0 } bytes => "0x" + Convert.ToHexString(bytes),
        _ => null
    };

    private static async Task RetryAsync(Func<Task> run, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + RetryWindow;
        while (true)
        {
            try
            {
                await run();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static async Task WaitForZeroAsync(ReplicaConnection runner, string sql, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (true)
        {
            var result = await runner.QueryAsync(sql, ct);
            var remaining = result.Rows.Count == 0 ? -1 : Convert.ToInt32(result.Rows[0][0]);
            if (remaining == 0)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Still {remaining} after {WaitLimit.TotalMinutes:0} minutes of waiting. Stopped here; the steps before this one ran, nothing after it did.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    [GeneratedRegex(@"\$\((cert:[^)]+)\)")]
    private static partial Regex Token();
}
