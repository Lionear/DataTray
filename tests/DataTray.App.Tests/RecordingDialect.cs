using DataTray.Sdk;

namespace DataTray.App.Tests;

/// <summary>Spells out the parts it was given, so a test sees exactly what reached QualifyName.</summary>
internal sealed class RecordingDialect : ISqlDialect
{
    public IReadOnlySet<string> Keywords => new HashSet<string>();
    public string QuoteIdentifier(string identifier) => $"[{identifier}]";
    public string QualifyName(string? database, string? schema, string table) => $"{database}|{schema}|{table}";
    public string Paginate(string sql, int limit, int offset, string? orderBy = null) => sql;
}
