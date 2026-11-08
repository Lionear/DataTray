namespace DataTray.Sdk.Extensibility;

/// <summary>A table or view with its columns and outgoing foreign keys.</summary>
public sealed record QueryTable(
    QueryTableRef Ref,
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<QueryForeignKey> ForeignKeys);
