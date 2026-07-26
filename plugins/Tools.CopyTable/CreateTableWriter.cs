using System.Text;

namespace SqlExplorer.Tools.CopyTable;

/// <summary>
/// Renders the DDL that recreates a <see cref="TableDef"/> on the target: the <c>CREATE TABLE</c> itself,
/// and — when the copy includes them — its secondary indexes and foreign keys. Identifiers and column specs
/// come from the shared <see cref="SqlDialect"/>, so the copy quotes and declares auto-numbering the way the
/// engine does (the target is the same engine as the source; the picker enforces that).
///
/// <para>What goes inline in the CREATE and what follows it is decided by the engine, not by taste: primary
/// key and unique constraints are always inline, indexes are always separate (no engine declares them in a
/// CREATE TABLE), and foreign keys are separate <c>ALTER TABLE … ADD CONSTRAINT</c> statements — except on
/// SQLite, which cannot add a constraint to an existing table at all, so its foreign keys have to be inline
/// or not exist.</para>
///
/// <para>Pure string work, unit-tested without a database. The data half of the copy is generated separately
/// by the SDK's <c>InsertScripter</c>.</para>
/// </summary>
public sealed class CreateTableWriter(SqlDialect dialect)
{
    /// <param name="keepIdentity">When true the copy preserves the source's identity values: identity columns
    /// become plain columns (their auto-numbering clause dropped) so the original values insert cleanly on any
    /// engine. When false the target regenerates them: the column keeps the engine's identity clause and is
    /// left out of the insert (see <see cref="ColumnsForInsert"/>).</param>
    /// <param name="includeForeignKeys">Only consulted on an engine that must declare foreign keys inline.</param>
    public string Build(TableDef table, bool keepIdentity, bool includeForeignKeys)
    {
        var body = new List<string>();
        body.AddRange(table.Columns.OrderBy(c => c.Ordinal).Select(c => dialect.ColumnSpec(c, !keepIdentity)));

        if (table.PrimaryKey is { Columns.Count: > 0 } pk)
        {
            body.Add(dialect.PrimaryKeyClause(pk, Cols(pk.Columns)));
        }

        body.AddRange(table.Uniques.Select(u =>
            $"CONSTRAINT {dialect.Quote(u.Name)} UNIQUE ({Cols(u.Columns)})"));

        // SQLite's ALTER TABLE can't add a constraint, so a foreign key it doesn't declare here can never be
        // created. Every other engine gets them as separate statements, after the rows are in.
        if (includeForeignKeys && !dialect.SupportsAlterConstraint)
        {
            body.AddRange(table.ForeignKeys.Select(fk =>
                $"CONSTRAINT {dialect.Quote(fk.Name)} FOREIGN KEY ({Cols(fk.Columns)}) " +
                $"REFERENCES {RefTable(fk)} ({Cols(fk.RefColumns)})"));
        }

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(dialect.QuoteTable(table)).Append(" (\n    ");
        sb.Append(string.Join(",\n    ", body));
        sb.Append("\n);");
        return sb.ToString();
    }

    /// <summary>The table's secondary indexes, one <c>CREATE INDEX</c> each. Empty when the source has none —
    /// the primary key and unique constraints are already in the CREATE, and their backing indexes are the
    /// engine's business.</summary>
    public IReadOnlyList<string> Indexes(TableDef table) =>
        table.Indexes
            .Select(i => $"CREATE {(i.Unique ? "UNIQUE " : "")}INDEX {dialect.Quote(i.Name)} " +
                         $"ON {dialect.QuoteTable(table)} ({Cols(i.Columns)});")
            .ToList();

    /// <summary>The table's foreign keys as separate statements, to run once its rows are in. Empty on an
    /// engine that had to declare them inline (SQLite) — <see cref="Build"/> already carried them.
    ///
    /// <para>Each one is a statement of its own on purpose: a foreign key points at another table, which the
    /// copy did not bring along, so it may legitimately not exist on the target. The caller runs them one at
    /// a time and reports how many landed rather than failing a copy that otherwise succeeded.</para></summary>
    public IReadOnlyList<string> ForeignKeys(TableDef table) =>
        dialect.SupportsAlterConstraint
            ? table.ForeignKeys
                .Select(fk => $"ALTER TABLE {dialect.QuoteTable(table)} ADD CONSTRAINT {dialect.Quote(fk.Name)} " +
                              $"FOREIGN KEY ({Cols(fk.Columns)}) REFERENCES {RefTable(fk)} ({Cols(fk.RefColumns)});")
                .ToList()
            : [];

    /// <summary>A <c>DROP TABLE IF EXISTS</c> for the target, guarding a re-copy. SQL Server got
    /// <c>DROP TABLE IF EXISTS</c> in 2016; the others have had it for years.</summary>
    public string DropIfExists(TableDef table) => $"DROP TABLE IF EXISTS {dialect.QuoteTable(table)};";

    public string QuoteTable(TableDef table) => dialect.QuoteTable(table);

    public string Quote(string identifier) => dialect.Quote(identifier);

    /// <summary>The columns actually written by the insert: all of them when <paramref name="keepIdentity"/>,
    /// or every non-identity column when the target regenerates the identity. Drives the source <c>SELECT</c>
    /// so the read and the insert line up.</summary>
    public static IReadOnlyList<ColumnDef> ColumnsForInsert(TableDef table, bool keepIdentity) =>
        keepIdentity ? table.Columns : table.Columns.Where(c => !c.IsIdentity).ToList();

    private string RefTable(ForeignKeyDef fk) =>
        string.IsNullOrEmpty(fk.RefSchema)
            ? dialect.Quote(fk.RefTable)
            : $"{dialect.Quote(fk.RefSchema)}.{dialect.Quote(fk.RefTable)}";

    private string Cols(IReadOnlyList<string> columns) => string.Join(", ", columns.Select(dialect.Quote));
}
