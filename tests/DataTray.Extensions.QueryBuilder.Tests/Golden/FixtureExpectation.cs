namespace DataTray.Extensions.QueryBuilder.Tests.Golden;

/// <summary>What a golden case expects for one dialect: the SQL, or (when Sql is null) the problem codes.</summary>
internal sealed record FixtureExpectation(string? Sql, string[] ProblemCodes);
