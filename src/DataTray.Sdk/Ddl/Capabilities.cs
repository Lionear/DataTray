using DataTray.Sdk.Schema;

namespace DataTray.Sdk.Ddl;

/// <summary>The kind of object DDL Create can build — a narrower, purpose-built set than
/// <see cref="DbNodeKind"/> (which describes tree shape, not what's creatable).</summary>
public enum DbObjectKind
{
    Database,
    Schema,
    Table,

    /// <summary>An index on an existing table. Unlike the three above, it is created <em>under</em> an
    /// object rather than beside one, so its <see cref="CreateObjectSpec"/> carries the target
    /// <see cref="CreateObjectSpec.Table"/> as well as a schema.</summary>
    Index
}

/// <summary>
/// Declares that a provider can create <see cref="Kind"/> objects, and under which tree-node kind the
/// "New …" action should appear (e.g. Table creation shows up on a <see cref="DbNodeKind.TableFolder"/>
/// node). <see cref="ParentNode"/> is null for the connection root itself (e.g. "New Database" on a
/// Postgres/MsSql connection) — the same null-means-root convention the tree already uses for
/// <c>TreeNodeViewModel.NodeKind</c>. A provider with no capabilities for a kind simply omits it — the
/// host hides the menu item.
/// </summary>
public sealed record CreateCapability(DbObjectKind Kind, DbNodeKind? ParentNode);

/// <summary>
/// One column in a new table, as entered by the user in the DDL Create dialog. <see cref="AutoIncrement"/>
/// is genuinely provider-specific — Postgres renders <c>GENERATED ALWAYS AS IDENTITY</c>, MySQL appends
/// <c>AUTO_INCREMENT</c>, SQL Server appends <c>IDENTITY(1,1)</c>, and SQLite folds it into the column
/// definition itself (<c>INTEGER PRIMARY KEY AUTOINCREMENT</c>, which also changes how the primary key
/// is declared) — so each provider decides how (or whether) to honour it in <c>BuildCreateStatement</c>.
/// </summary>
public sealed record NewColumnSpec(string Name, string Type, bool Nullable, bool PrimaryKey, bool AutoIncrement);

/// <summary>One column of a new index, in key order. <see cref="Descending"/> is per column, as every
/// engine here allows — a two-column index can be ascending on one and descending on the other, and that
/// ordering is the whole point of the index for a query that sorts that way.</summary>
public sealed record NewIndexColumnSpec(string Name, bool Descending);

/// <summary>
/// Declarative input for DDL Create, collected by the host and handed to
/// <see cref="IDbProvider.BuildCreateStatement"/>. <see cref="Schema"/> is the parent schema for a
/// <see cref="DbObjectKind.Table"/>; <see cref="Columns"/> is populated only for tables.
/// </summary>
/// <param name="Table">The table an index is created on — meaningless for the other kinds, where
/// <see cref="Name"/> is itself the object being created.</param>
/// <param name="IndexColumns">The index's key columns, in order. Empty for the other kinds.</param>
/// <param name="Unique">Whether the index enforces uniqueness. Every engine the host ships spells this
/// the same way (<c>CREATE UNIQUE INDEX</c>), unlike clustering, which is why that one is not here: a
/// SQL Server user who wants CLUSTERED types it into the dialog's editable SQL preview, rather than every
/// other engine growing a checkbox that does nothing.</param>
public sealed record CreateObjectSpec(
    DbObjectKind Kind,
    string Name,
    string? Schema,
    IReadOnlyList<NewColumnSpec> Columns,
    string? Table = null,
    IReadOnlyList<NewIndexColumnSpec>? IndexColumns = null,
    bool Unique = false)
{
    public IReadOnlyList<NewIndexColumnSpec> IndexColumns { get; init; } = IndexColumns ?? [];
}
