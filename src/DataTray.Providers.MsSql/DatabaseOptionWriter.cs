using DataTray.Sdk;

namespace DataTray.Providers.MsSql;

/// <summary>
/// One editable database option: how it reads, how it is spelled as an <c>ALTER DATABASE … SET</c>, and
/// whether changing it needs everyone else out of the database first.
/// </summary>
/// <param name="Key">The <c>PropPage</c> row key, so the page and the writer name the same thing once.</param>
/// <param name="Clause">The SET clause with <c>{0}</c> where the value goes, e.g. <c>AUTO_CLOSE {0}</c>.</param>
/// <param name="Exclusive">
/// True when the option cannot be set while other sessions are connected. SQL Server does not fail these —
/// it <em>blocks</em>, indefinitely, until every other connection closes, which looks exactly like a hung
/// application. WITH ROLLBACK IMMEDIATE is the way out, and it disconnects those sessions and rolls back
/// what they were doing, so it is never added silently.
/// </param>
internal sealed record DatabaseOption(string Key, string Clause, bool Exclusive = false);

/// <summary>
/// Turns the difference between two readings of the Options page into <c>ALTER DATABASE</c> statements.
/// Pure string work over a snapshot and a diff — the same shape <c>LoginPropertiesViewModel</c> uses, and
/// the reason it is here rather than in the view is that "which statement does this checkbox emit" is
/// exactly what wants pinning: getting one wrong means silently changing the wrong setting on someone's
/// database.
/// </summary>
internal static class DatabaseOptionWriter
{
    /// <summary>
    /// Every option the dialog can write, keyed by its page row. Ordered as the page shows them, so a
    /// script of several changes reads top-to-bottom like the dialog does.
    /// </summary>
    public static readonly IReadOnlyList<DatabaseOption> Options =
    [
        new("recovery", "RECOVERY {0}"),
        new("autoClose", "AUTO_CLOSE {0}"),
        new("autoCreateStats", "AUTO_CREATE_STATISTICS {0}"),
        new("autoShrink", "AUTO_SHRINK {0}"),
        new("autoUpdateStats", "AUTO_UPDATE_STATISTICS {0}"),
        new("autoUpdateStatsAsync", "AUTO_UPDATE_STATISTICS_ASYNC {0}"),
        new("cursorClose", "CURSOR_CLOSE_ON_COMMIT {0}"),
        new("cursorDefault", "CURSOR_DEFAULT {0}"),
        new("pageVerify", "PAGE_VERIFY {0}"),
        new("targetRecovery", "TARGET_RECOVERY_TIME = {0} SECONDS"),
        new("ansiNullDefault", "ANSI_NULL_DEFAULT {0}"),
        new("ansiNulls", "ANSI_NULLS {0}"),
        new("ansiPadding", "ANSI_PADDING {0}"),
        new("ansiWarnings", "ANSI_WARNINGS {0}"),
        new("arithAbort", "ARITHABORT {0}"),
        new("concatNull", "CONCAT_NULL_YIELDS_NULL {0}"),
        new("numericRoundAbort", "NUMERIC_ROUNDABORT {0}"),
        new("quotedIdentifier", "QUOTED_IDENTIFIER {0}"),
        new("recursiveTriggers", "RECURSIVE_TRIGGERS {0}"),
        new("trustworthy", "TRUSTWORTHY {0}"),
        new("dateCorrelation", "DATE_CORRELATION_OPTIMIZATION {0}"),
        new("parameterization", "PARAMETERIZATION {0}"),
        new("delayedDurability", "DELAYED_DURABILITY = {0}"),
        new("brokerPriority", "HONOR_BROKER_PRIORITY {0}"),
        // ENABLE_BROKER waits for every other session to leave; DISABLE_BROKER does not, but pairing them
        // as one row means the dialog would sometimes hang and sometimes not for the same checkbox.
        new("broker", "{0}", Exclusive: true),
        new("snapshotIso", "ALLOW_SNAPSHOT_ISOLATION {0}"),
        new("rcsi", "READ_COMMITTED_SNAPSHOT {0}", Exclusive: true),
        new("readOnly", "{0}", Exclusive: true),
        new("userAccess", "{0}", Exclusive: true)
    ];

    /// <summary>
    /// The statements for every row whose value differs between <paramref name="original"/> and
    /// <paramref name="wanted"/>. One statement per option rather than a comma-separated SET: the exclusive
    /// ones carry their own termination clause, and a single failure in a combined statement takes every
    /// other change with it.
    /// </summary>
    /// <param name="rollbackImmediate">
    /// Whether the user has agreed to disconnect other sessions. When false, an option that needs it is left
    /// out entirely rather than emitted without the clause — emitting it would block until every other
    /// connection closed, with no indication that is what is happening.
    /// </param>
    public static IReadOnlyList<string> Alter(
        ISqlDialect dialect,
        string database,
        IReadOnlyDictionary<string, string> original,
        IReadOnlyDictionary<string, string> wanted,
        bool rollbackImmediate)
    {
        var name = dialect.QuoteIdentifier(database);
        var statements = new List<string>();

        foreach (var option in Options)
        {
            if (!wanted.TryGetValue(option.Key, out var value)
                || (original.TryGetValue(option.Key, out var was) && was == value))
            {
                continue;
            }

            if (option.Exclusive && !rollbackImmediate)
            {
                continue;
            }

            var termination = option.Exclusive ? " WITH ROLLBACK IMMEDIATE" : "";
            statements.Add($"ALTER DATABASE {name} SET {string.Format(option.Clause, value)}{termination}");
        }

        return statements;
    }

    /// <summary>Whether any changed row needs other sessions disconnected — what the dialog asks about
    /// before it will run them, and what it greys those rows out for until answered.</summary>
    public static bool NeedsExclusiveAccess(
        IReadOnlyDictionary<string, string> original, IReadOnlyDictionary<string, string> wanted) =>
        Options.Any(o => o.Exclusive
            && wanted.TryGetValue(o.Key, out var value)
            && original.TryGetValue(o.Key, out var was)
            && was != value);

    /// <summary>
    /// The <c>sp_*extendedproperty</c> calls for the database itself. At class 0 there are no level
    /// arguments at all, which is what makes this the least dangerous write in the dialog — no ALTER
    /// DATABASE semantics, nothing to take offline.
    /// </summary>
    public static IReadOnlyList<string> ExtendedProperties(
        IReadOnlyDictionary<string, string> original, IReadOnlyDictionary<string, string> wanted)
    {
        var statements = new List<string>();

        foreach (var (name, value) in wanted)
        {
            if (!original.TryGetValue(name, out var was))
            {
                statements.Add($"EXEC sp_addextendedproperty @name = {Literal(name)}, @value = {Literal(value)}");
            }
            else if (was != value)
            {
                statements.Add($"EXEC sp_updateextendedproperty @name = {Literal(name)}, @value = {Literal(value)}");
            }
        }

        foreach (var name in original.Keys.Where(k => !wanted.ContainsKey(k)))
        {
            statements.Add($"EXEC sp_dropextendedproperty @name = {Literal(name)}");
        }

        return statements;
    }

    /// <summary>
    /// <c>ALTER DATABASE … MODIFY FILE</c> for a file whose growth or maximum size changed. Growth is stated
    /// in the unit the user picked; SQL Server stores it in 8 KB pages either way, which is why the dialog
    /// never shows that number.
    /// </summary>
    /// <param name="growthPercent">Whether <paramref name="growth"/> is a percentage rather than megabytes.</param>
    /// <param name="maxSizeMb">Null for UNLIMITED, 0 for a file that may not grow past its current size.</param>
    public static string ModifyFile(
        ISqlDialect dialect, string database, string file, int growth, bool growthPercent, int? maxSizeMb)
    {
        var growthClause = growth <= 0
            ? "FILEGROWTH = 0"
            : $"FILEGROWTH = {growth}{(growthPercent ? "%" : "MB")}";

        var maxClause = maxSizeMb switch
        {
            null => "MAXSIZE = UNLIMITED",
            <= 0 => "MAXSIZE = UNLIMITED",
            _ => $"MAXSIZE = {maxSizeMb}MB"
        };

        return $"ALTER DATABASE {dialect.QuoteIdentifier(database)} MODIFY FILE "
            + $"(NAME = {Literal(file)}, {growthClause}, {maxClause})";
    }

    private static string Literal(string value) => $"N'{value.Replace("'", "''")}'";
}
