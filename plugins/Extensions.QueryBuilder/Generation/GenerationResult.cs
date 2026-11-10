namespace DataTray.Extensions.QueryBuilder.Generation;

/// <summary>The SQL for a statement, or the problems that keep the dialect from expressing it (then Sql is null).</summary>
public sealed record GenerationResult(string? Sql, IReadOnlyList<GenerationProblem> Problems);
