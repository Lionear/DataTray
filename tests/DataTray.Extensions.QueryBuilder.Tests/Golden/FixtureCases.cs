using System.Text.Json;
using DataTray.Extensions.QueryBuilder.Model;

namespace DataTray.Extensions.QueryBuilder.Tests.Golden;

// Reads the golden fixtures (copied next to the test assembly); data access only.
internal static class FixtureCases
{
    public static readonly string[] Dialects = ["sqlserver", "postgres", "mysql", "sqlite"];

    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string SchemaSql => Read(Path.Combine(Root, "_schema", "sqlite.sql"));

    public static string[] CaseNames() =>
        Directory.GetDirectories(Root).Select(Path.GetFileName).Where(n => !n!.StartsWith('_')).Order().ToArray()!;

    public static string Version(string dialect) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Root, "_versions.json")))![dialect];

    public static Statement Model(string name) => QueryModelJson.Deserialize(Read(Path.Combine(Root, name, "model.json")));

    public static FixtureExpectation Expected(string name, string dialect)
    {
        var sqlFile = Path.Combine(Root, name, dialect + ".sql");
        var problemsFile = Path.Combine(Root, name, dialect + ".problems.txt");
        return File.Exists(sqlFile)
            ? new FixtureExpectation(Read(sqlFile), [])
            : new FixtureExpectation(null, Read(problemsFile).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string Sql(string name, string dialect) => Read(Path.Combine(Root, name, dialect + ".sql"));

    public static TheoryData<string, string> AllCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var dialect in Dialects)
        {
            foreach (var name in CaseNames())
            {
                data.Add(dialect, name);
            }
        }
        return data;
    }

    public static TheoryData<string, string> SqlCases(params string[] dialects) => SqlCases(_ => true, dialects);

    // Cases whose golden for these dialects is SQL (not a problem list).
    public static TheoryData<string, string> SqlCases(Func<string, bool> include, params string[] dialects)
    {
        var data = new TheoryData<string, string>();
        foreach (var dialect in dialects)
        {
            foreach (var name in CaseNames().Where(include).Where(n => File.Exists(Path.Combine(Root, n, dialect + ".sql"))))
            {
                data.Add(dialect, name);
            }
        }
        return data;
    }

    private static string Read(string path) => File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd('\n');
}
