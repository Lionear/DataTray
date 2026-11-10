namespace DataTray.Extensions.QueryBuilder.Reading;

internal static class SqlReaders
{
    public static ISqlReader For(string providerId) => providerId switch
    {
        "postgres" or "mysql" or "sqlite" => new SqlParserCsReader(providerId),
        "sqlserver" => new ScriptDomReader(),
        _ => throw new NotSupportedException($"No SQL reader for provider '{providerId}' yet."),
    };
}
