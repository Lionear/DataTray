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

/// <summary>
/// The two options that are not settings of an index at all — they steer the one operation that is about to
/// run and are stored nowhere, so there is nothing to read back and nothing to diff. SSMS shows them on the
/// Options page beside settings that persist, which is exactly why the dialog labels them differently.
/// Kept out of <see cref="IndexDefinition"/> deliberately: putting them there would make "the user typed a
/// MAXDOP" look like a changed index and rebuild one that nobody asked to change.
/// </summary>
/// <param name="MaxDop">0 means "no MAXDOP hint", which is not the same as 1.</param>
internal sealed record RebuildOptions(int MaxDop = 0, bool SortInTempDb = false);

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
    /// <para>When only the options that <c>ALTER INDEX … SET</c> accepts have changed, that is what runs:
    /// rebuilding an index to flip ALLOW_PAGE_LOCKS would read every page of it for a metadata change. Note
    /// the asymmetry that makes this safe — SET touches only what it names, where DROP_EXISTING resets
    /// everything it is not told.</para>
    /// </remarks>
    public static IReadOnlyList<string> Alter(
        ISqlDialect dialect, IndexDefinition? original, IndexDefinition wanted, RebuildOptions? rebuild = null)
    {
        if (original is null)
        {
            return [Create(dialect, wanted, dropExisting: false, rebuild)];
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
            statements.Add(Create(dialect, wanted, dropExisting: false, rebuild));
        }
        else if (NeedsRebuild(original, wanted))
        {
            statements.Add(Create(dialect, wanted, dropExisting: true, rebuild));
        }
        else
        {
            statements.Add(Set(dialect, original, wanted));
        }

        return statements;
    }

    /// <summary>
    /// Whether the difference between these two can only be made by writing the index again. The structural
    /// half is obvious — columns, uniqueness, the filter, the filegroup. Fill factor and pad index are the
    /// unobvious half: <c>ALTER INDEX … SET</c> rejects FILLFACTOR at <em>parse</em> time (Msg 155), so a
    /// batch containing it fails wholesale rather than falling back.
    /// </summary>
    private static bool NeedsRebuild(IndexDefinition a, IndexDefinition b) =>
        !a.Columns.SequenceEqual(b.Columns)
        || a.Unique != b.Unique
        || a.Filter != b.Filter
        || a.DataSpace != b.DataSpace
        || a.PartitionColumn != b.PartitionColumn
        || a.PadIndex != b.PadIndex
        || a.FillFactor != b.FillFactor;

    // Only the options that actually changed — unlike a rebuild, SET leaves alone what it does not name, so
    // restating the unchanged ones would only make the script harder to read.
    private static string Set(ISqlDialect dialect, IndexDefinition original, IndexDefinition wanted)
    {
        var changes = new List<string>();
        if (original.AllowRowLocks != wanted.AllowRowLocks)
        {
            changes.Add($"ALLOW_ROW_LOCKS = {OnOff(wanted.AllowRowLocks)}");
        }

        if (original.AllowPageLocks != wanted.AllowPageLocks)
        {
            changes.Add($"ALLOW_PAGE_LOCKS = {OnOff(wanted.AllowPageLocks)}");
        }

        if (original.IgnoreDupKey != wanted.IgnoreDupKey)
        {
            changes.Add($"IGNORE_DUP_KEY = {OnOff(wanted.Unique && wanted.IgnoreDupKey)}");
        }

        if (original.StatisticsNoRecompute != wanted.StatisticsNoRecompute)
        {
            changes.Add($"STATISTICS_NORECOMPUTE = {OnOff(wanted.StatisticsNoRecompute)}");
        }

        if (original.OptimizeForSequentialKey != wanted.OptimizeForSequentialKey
            && wanted.OptimizeForSequentialKey is { } optimize)
        {
            changes.Add($"OPTIMIZE_FOR_SEQUENTIAL_KEY = {OnOff(optimize)}");
        }

        return $"ALTER INDEX {dialect.QuoteIdentifier(wanted.Name)} ON {Qualified(dialect, wanted)} " +
            $"SET ({string.Join(", ", changes)})";
    }

    /// <summary>
    /// The same statements as <see cref="Alter"/>, as one script to drop in a query tab. A filtered index
    /// needs <c>SET QUOTED_IDENTIFIER ON</c> to rebuild, and fails with a message about indexed views and
    /// computed columns that mentions no filter — SqlClient sets it, so what OK runs needs no preamble, but
    /// a script that lands in sqlcmd or a scheduled job does.
    /// </summary>
    public static string Script(
        ISqlDialect dialect, IndexDefinition? original, IndexDefinition wanted, RebuildOptions? rebuild = null) =>
        Script(Alter(dialect, original, wanted, rebuild),
            filtered: wanted.Filter is { Length: > 0 } || original?.Filter is { Length: > 0 });

    /// <inheritdoc cref="Script(ISqlDialect, IndexDefinition, IndexDefinition, RebuildOptions)"/>
    /// <param name="statements">Already-built statements, for a caller that appends its own — the dialog
    /// adds the extended-property calls after the index itself.</param>
    /// <param name="filtered">Whether either side of the change carries a filter.</param>
    public static string Script(IReadOnlyList<string> statements, bool filtered)
    {
        if (statements.Count == 0)
        {
            return "-- Nothing to change.";
        }

        var preamble = filtered ? "SET QUOTED_IDENTIFIER ON;\r\nGO\r\n\r\n" : string.Empty;

        return preamble + string.Join("\r\nGO\r\n\r\n", statements) + "\r\nGO\r\n";
    }

    public static string Create(
        ISqlDialect dialect, IndexDefinition index, bool dropExisting, RebuildOptions? rebuild = null)
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

        sql.Append($" WITH ({string.Join(", ", Options(index, dropExisting, rebuild))})");

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
    private static IEnumerable<string> Options(IndexDefinition index, bool dropExisting, RebuildOptions? rebuild)
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

        // Operation-only, and last for that reason: these steer this one build and are stored nowhere, so
        // re-reading the index afterwards will not show them.
        if (rebuild?.SortInTempDb == true)
        {
            yield return "SORT_IN_TEMPDB = ON";
        }

        if (rebuild is { MaxDop: > 0 } options)
        {
            yield return $"MAXDOP = {options.MaxDop}";
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

    /// <summary>
    /// The <c>sp_*extendedproperty</c> calls that turn <paramref name="original"/> into
    /// <paramref name="wanted"/>. Separate from <see cref="Alter"/> and appended after it, because they
    /// address the index by name: on a create there is nothing to hang them off until it exists, and after a
    /// rename the old name is gone.
    /// </summary>
    /// <remarks>
    /// Three procedures rather than one upsert — SQL Server has no upsert here, and add on an existing name
    /// fails rather than replacing. An index sits at level 2, so all three levels have to be named; the
    /// schema falls back to dbo, which is where an unqualified table is.
    /// </remarks>
    public static IReadOnlyList<string> ExtendedProperties(
        string? schema,
        string table,
        string index,
        IReadOnlyDictionary<string, string> original,
        IReadOnlyDictionary<string, string> wanted)
    {
        var levels = $"@level0type = N'SCHEMA', @level0name = {Literal(string.IsNullOrEmpty(schema) ? "dbo" : schema)}, "
            + $"@level1type = N'TABLE', @level1name = {Literal(table)}, "
            + $"@level2type = N'INDEX', @level2name = {Literal(index)}";

        var statements = new List<string>();

        foreach (var (name, value) in wanted)
        {
            if (!original.TryGetValue(name, out var was))
            {
                statements.Add($"EXEC sp_addextendedproperty @name = {Literal(name)}, @value = {Literal(value)}, {levels}");
            }
            else if (was != value)
            {
                statements.Add($"EXEC sp_updateextendedproperty @name = {Literal(name)}, @value = {Literal(value)}, {levels}");
            }
        }

        foreach (var name in original.Keys.Where(k => !wanted.ContainsKey(k)))
        {
            statements.Add($"EXEC sp_dropextendedproperty @name = {Literal(name)}, {levels}");
        }

        return statements;
    }

    /// <summary>
    /// One row of fragmentation for <paramref name="index"/>, from <c>sys.dm_db_index_physical_stats</c>.
    /// </summary>
    /// <param name="detailed">
    /// <c>LIMITED</c> reads the parent level of the b-tree; <c>DETAILED</c> reads every page, which is why
    /// this dialog opens on the cheap one and puts the other behind a button. The columns are the same
    /// either way — <c>LIMITED</c> simply returns NULL for page fullness, record counts, ghost rows and row
    /// sizes, which is most of what the page shows.
    /// </param>
    /// <remarks>
    /// <c>index_level = 0</c> is not optional: DETAILED returns one row per b-tree level, and without the
    /// filter the page would show an intermediate level's numbers. A partitioned index is folded to one row
    /// the same way the maintenance dialog folds it — MAX of the fragmentation, since the worst partition is
    /// what makes a rebuild worth running, and SUM of the counts.
    /// </remarks>
    public static string Fragmentation(ISqlDialect dialect, string? schema, string table, string index, bool detailed)
    {
        var qualified = string.IsNullOrEmpty(schema)
            ? dialect.QuoteIdentifier(table)
            : $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(table)}";

        return $"""
            SELECT CAST(MAX(ps.avg_fragmentation_in_percent) AS decimal(5,2)),
                   SUM(ps.page_count),
                   SUM(ps.fragment_count),
                   CAST(AVG(ps.avg_fragment_size_in_pages) AS decimal(9,2)),
                   CAST(AVG(ps.avg_page_space_used_in_percent) AS decimal(5,2)),
                   SUM(ps.record_count),
                   SUM(ps.ghost_record_count),
                   CAST(AVG(ps.avg_record_size_in_bytes) AS decimal(9,2))
            FROM sys.dm_db_index_physical_stats(
                DB_ID(), OBJECT_ID({Literal(qualified)}), NULL, NULL, '{(detailed ? "DETAILED" : "LIMITED")}') AS ps
            JOIN sys.indexes AS i
                ON i.object_id = ps.object_id AND i.index_id = ps.index_id
            WHERE ps.index_level = 0 AND i.name = {Literal(index)}
            """;
    }

    /// <summary>
    /// A filter as the user typed it, from the form <c>sys.indexes</c> stores. The server normalises
    /// "Slot &gt; 0" to "([Slot]&gt;(0))"; the outer pair is its own, and leaving it on means the Filter box
    /// shows a predicate the user did not write and — worse — every reopen would compare as changed.
    /// </summary>
    public static string? StripOuterParentheses(string? filter)
    {
        if (filter is not { Length: > 1 } || filter[0] != '(' || filter[^1] != ')')
        {
            return filter;
        }

        // Only when the opening bracket is the one the final bracket closes: "(a) AND (b)" also starts with
        // "(" and ends with ")", and stripping it would produce "a) AND (b".
        var depth = 0;
        for (var i = 0; i < filter.Length; i++)
        {
            depth += filter[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0 && i < filter.Length - 1)
            {
                return filter;
            }
        }

        return filter[1..^1];
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static string Literal(string value) => $"N'{value.Replace("'", "''")}'";
}
