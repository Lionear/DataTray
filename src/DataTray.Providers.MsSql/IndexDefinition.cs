using DataTray.Sdk;

namespace DataTray.Providers.MsSql;

/// <summary>One column of an index: a key column in its key order, or an included column.</summary>
/// <param name="Descending">Meaningless for an included column — INCLUDE carries no sort order.</param>
internal sealed record IndexColumn(string Name, bool Descending = false, bool Included = false);

/// <summary>
/// Everything CREATE INDEX needs, whether read from the catalog for an edit or collected by the dialog for
/// a new index. Kept separate from the view so the statement building — the part where a wrong option
/// silently changes an index nobody looks at again — is plain string work and tested directly.
/// </summary>
internal sealed record IndexDefinition
{
    public string? Schema { get; init; }
    public required string Table { get; init; }
    public required string Name { get; init; }
    public bool Clustered { get; init; }
    public bool Unique { get; init; }
    public IReadOnlyList<IndexColumn> Columns { get; init; } = [];

    // ── The option set ────────────────────────────────────────────────────────────────────────────────
    //
    // These are read even though phase 1 has no Options page, and re-emitted on every rebuild. DROP_EXISTING
    // silently resets every option the statement does not restate: change one key column with these left out
    // and PAD_INDEX, ALLOW_PAGE_LOCKS, STATISTICS_NORECOMPUTE and OPTIMIZE_FOR_SEQUENTIAL_KEY all quietly
    // revert to their defaults. Carrying them through is not the Options page arriving early — it is the
    // condition for editing an index at all without undoing settings the user never opened.

    public bool PadIndex { get; init; }

    /// <summary>0 means "server default" and is not a legal FILLFACTOR, so it is omitted rather than emitted.</summary>
    public int FillFactor { get; init; }

    public bool IgnoreDupKey { get; init; }
    public bool StatisticsNoRecompute { get; init; }
    public bool AllowRowLocks { get; init; } = true;
    public bool AllowPageLocks { get; init; } = true;

    /// <summary>Null on a server that predates SQL Server 2019, where the option does not parse at all.</summary>
    public bool? OptimizeForSequentialKey { get; init; }

    /// <summary>The filter predicate as <c>sys.indexes</c> stores it — already parenthesised, without WHERE.</summary>
    public string? Filter { get; init; }

    /// <summary>The filegroup or partition scheme the index lives on. Null leaves the ON clause off, which
    /// puts a new index on the default filegroup.</summary>
    public string? DataSpace { get; init; }

    /// <summary>Set when <see cref="DataSpace"/> is a partition scheme: the column it partitions on, which
    /// the ON clause must name. Emitting a partition scheme without it is a syntax error, and falling back
    /// to a bare filegroup name would silently move a partitioned index onto one filegroup.</summary>
    public string? PartitionColumn { get; init; }

    public IEnumerable<IndexColumn> Keys => Columns.Where(c => !c.Included);
    public IEnumerable<IndexColumn> Included => Columns.Where(c => c.Included);

    /// <summary>Whether <paramref name="other"/> describes the same index, ignoring the name. The generated
    /// record equality is not enough: <see cref="Columns"/> is a list, so it compares by reference and two
    /// definitions read from the same catalog rows would never match — which would rebuild the index every
    /// time OK is pressed.</summary>
    public bool SameShapeAs(IndexDefinition other) =>
        this with { Name = other.Name, Columns = other.Columns } == other
        && Columns.SequenceEqual(other.Columns);
}

/// <summary>Builds the T-SQL behind the Index Properties dialog — both what OK runs and what Script shows.</summary>
internal static class IndexScript
{
    /// <summary>
    /// The statements that turn <paramref name="original"/> into <paramref name="wanted"/>, or create it
    /// outright when <paramref name="original"/> is null. Empty when nothing changed, so pressing OK on an
    /// untouched dialog does not rebuild an index for nothing.
    /// </summary>
    /// <remarks>
    /// A rename is <c>sp_rename</c>, not part of the rebuild: DROP_EXISTING drops the index named by the
    /// statement, so creating under a new name would build a second index and leave the first one behind.
    /// It comes first, so the rebuild that may follow addresses the index by the name it now has.
    /// <para>Changing clustered to nonclustered or back is DROP + CREATE rather than DROP_EXISTING, which
    /// refuses some of those conversions. Dropping a clustered index rebuilds the table, but that cost is
    /// inherent to the change being asked for — and the alternative is a statement the server rejects.</para>
    /// </remarks>
    public static IReadOnlyList<string> Alter(ISqlDialect dialect, IndexDefinition? original, IndexDefinition wanted)
    {
        if (original is null)
        {
            return [Create(dialect, wanted, dropExisting: false)];
        }

        var statements = new List<string>();
        if (!string.Equals(original.Name, wanted.Name, StringComparison.Ordinal))
        {
            statements.Add(Rename(dialect, original, wanted.Name));
        }

        // Compared ignoring the name so a pure rename does not also look like a definition change.
        if (original.SameShapeAs(wanted))
        {
            return statements;
        }

        if (original.Clustered != wanted.Clustered)
        {
            statements.Add($"DROP INDEX {dialect.QuoteIdentifier(wanted.Name)} ON {Qualified(dialect, wanted)}");
            statements.Add(Create(dialect, wanted, dropExisting: false));
        }
        else
        {
            statements.Add(Create(dialect, wanted, dropExisting: true));
        }

        return statements;
    }

    /// <summary>
    /// The same statements as <see cref="Alter"/>, as one script to drop in a query tab. A filtered index
    /// needs <c>SET QUOTED_IDENTIFIER ON</c> to rebuild, and fails with a message about indexed views and
    /// computed columns that mentions no filter — SqlClient sets it, so what OK runs needs no preamble, but
    /// a script that lands in sqlcmd or a scheduled job does.
    /// </summary>
    public static string Script(ISqlDialect dialect, IndexDefinition? original, IndexDefinition wanted)
    {
        var statements = Alter(dialect, original, wanted);
        if (statements.Count == 0)
        {
            return "-- Nothing to change.";
        }

        var preamble = wanted.Filter is { Length: > 0 } || original?.Filter is { Length: > 0 }
            ? "SET QUOTED_IDENTIFIER ON;\r\nGO\r\n\r\n"
            : string.Empty;

        return preamble + string.Join("\r\nGO\r\n\r\n", statements) + "\r\nGO\r\n";
    }

    public static string Create(ISqlDialect dialect, IndexDefinition index, bool dropExisting)
    {
        var keys = index.Keys.ToList();
        if (keys.Count == 0)
        {
            throw new InvalidOperationException("An index needs at least one key column.");
        }

        var unique = index.Unique ? "UNIQUE " : string.Empty;
        var clustered = index.Clustered ? "CLUSTERED" : "NONCLUSTERED";
        // Sort order is spelled out on every key column here, unlike the host's generic CREATE INDEX: this
        // statement is also what Script shows, and an index the user has just ordered by hand reads better
        // when the column list says so rather than leaving the reader to know that bare means ascending.
        var keyList = keys.Select(c => $"{dialect.QuoteIdentifier(c.Name)} {(c.Descending ? "DESC" : "ASC")}");

        var sql = new System.Text.StringBuilder()
            .Append($"CREATE {unique}{clustered} INDEX {dialect.QuoteIdentifier(index.Name)}")
            .Append($" ON {Qualified(dialect, index)}")
            .Append($" ({string.Join(", ", keyList)})");

        var included = index.Included.ToList();
        if (included.Count > 0)
        {
            sql.Append($" INCLUDE ({string.Join(", ", included.Select(c => dialect.QuoteIdentifier(c.Name)))})");
        }

        if (index.Filter is { Length: > 0 } filter)
        {
            sql.Append($" WHERE {filter}");
        }

        sql.Append($" WITH ({string.Join(", ", Options(index, dropExisting))})");

        if (index.DataSpace is { Length: > 0 } space)
        {
            sql.Append($" ON {dialect.QuoteIdentifier(space)}");
            if (index.PartitionColumn is { Length: > 0 } column)
            {
                sql.Append($" ({dialect.QuoteIdentifier(column)})");
            }
        }

        return sql.ToString();
    }

    // Always the complete set, in the order SSMS scripts them. See the note on IndexDefinition's options:
    // anything left out here is reset by DROP_EXISTING, so "unchanged" options are exactly the ones that
    // must be restated.
    private static IEnumerable<string> Options(IndexDefinition index, bool dropExisting)
    {
        yield return $"PAD_INDEX = {OnOff(index.PadIndex)}";
        yield return $"STATISTICS_NORECOMPUTE = {OnOff(index.StatisticsNoRecompute)}";
        // IGNORE_DUP_KEY only exists on a unique index; ON for a non-unique one is rejected outright.
        yield return $"IGNORE_DUP_KEY = {OnOff(index.Unique && index.IgnoreDupKey)}";
        yield return $"ALLOW_ROW_LOCKS = {OnOff(index.AllowRowLocks)}";
        yield return $"ALLOW_PAGE_LOCKS = {OnOff(index.AllowPageLocks)}";

        if (index.OptimizeForSequentialKey is { } optimize)
        {
            yield return $"OPTIMIZE_FOR_SEQUENTIAL_KEY = {OnOff(optimize)}";
        }

        if (index.FillFactor > 0)
        {
            yield return $"FILLFACTOR = {index.FillFactor}";
        }

        if (dropExisting)
        {
            yield return "DROP_EXISTING = ON";
        }
    }

    // sp_rename takes its arguments as text, so the object is named as a three-part string rather than as
    // quoted identifiers — and the new name is bare, since qualifying it renames the index to a name with
    // dots in it. @objtype = 'INDEX' is what stops it looking for a table called schema.table.index.
    private static string Rename(ISqlDialect dialect, IndexDefinition index, string newName) =>
        $"EXEC sp_rename {Literal($"{Qualified(dialect, index)}.{dialect.QuoteIdentifier(index.Name)}")}, " +
        $"{Literal(newName)}, N'INDEX'";

    private static string Qualified(ISqlDialect dialect, IndexDefinition index) =>
        string.IsNullOrEmpty(index.Schema)
            ? dialect.QuoteIdentifier(index.Table)
            : $"{dialect.QuoteIdentifier(index.Schema)}.{dialect.QuoteIdentifier(index.Table)}";

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static string Literal(string value) => $"N'{value.Replace("'", "''")}'";
}
