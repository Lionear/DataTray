namespace DataTray.Sdk.Extensibility;

/// <summary>One column of a foreign key, pointing at a column of <see cref="ReferencedTable"/>.</summary>
public sealed record QueryForeignKey(string Column, string ReferencedTable, string ReferencedColumn);
