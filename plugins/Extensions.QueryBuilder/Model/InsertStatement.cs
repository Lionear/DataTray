namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record InsertStatement(With? With, TableName Target, ValueList<string> Columns, InsertSource Source, Upsert? OnExisting, Returning? Returning) : Statement;
