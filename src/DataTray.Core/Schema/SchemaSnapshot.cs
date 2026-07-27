using DataTray.Sdk;

namespace DataTray.Core.Schema;

/// <summary>A single browsable relation (table or view) captured in a schema snapshot.</summary>
public sealed record SchemaObject
{
    /// <summary><see cref="DbNodeKind.Table"/> or <see cref="DbNodeKind.View"/>.</summary>
    public required DbNodeKind Kind { get; init; }

    /// <summary>Owning database/catalog, if the engine has one (null for SQLite/MySQL).</summary>
    public string? Database { get; init; }

    /// <summary>Owning schema, if the engine has one (null for schema-less engines).</summary>
    public string? Schema { get; init; }

    public required string Name { get; init; }

    public IReadOnlyList<SchemaColumn> Columns { get; init; } = [];

    /// <summary>The relation's outgoing foreign keys — used to suggest JOIN conditions (SE-149 phase 3).</summary>
    public IReadOnlyList<SchemaForeignKey> ForeignKeys { get; init; } = [];

    /// <summary><c>schema.name</c> when schema-qualified, otherwise just the name — for display/search.</summary>
    public string QualifiedName => Schema is { Length: > 0 } schema ? $"{schema}.{Name}" : Name;
}

/// <summary>A column of a <see cref="SchemaObject"/>: its name and (optional) declared type.</summary>
public sealed record SchemaColumn(string Name, string? Type);

/// <summary>One outgoing foreign key: this relation's <see cref="Column"/> references
/// <see cref="ReferencedColumn"/> on <see cref="ReferencedTable"/> — the basis for a JOIN-condition hint.</summary>
public sealed record SchemaForeignKey(string Column, string ReferencedTable, string ReferencedColumn);

/// <summary>
/// An immutable, per-connection picture of the reachable tables/views and their columns, built once
/// by walking the lazy provider tree at connect. Feeds object-search (1.2) and schema-aware
/// completion (1.3) without re-hitting the database on every keystroke.
/// </summary>
public sealed class SchemaSnapshot(IReadOnlyList<SchemaObject> objects)
{
    public static readonly SchemaSnapshot Empty = new([]);

    public IReadOnlyList<SchemaObject> Objects { get; } = objects;
}
