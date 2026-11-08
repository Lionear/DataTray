namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Upsert(ValueList<string> ConflictColumns, ValueList<Assignment>? Update);
