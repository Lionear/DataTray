using DataTray.Core.Schema;
using DataTray.Sdk;

namespace DataTray.Core.Completion;

public enum CompletionKind
{
    Table,
    Column,
    Function,
    Join,
    Keyword
}

/// <summary>One completion suggestion: the text to insert, its kind, and a short detail
/// (column type, "table"/"view"/"cte", or "keyword") shown alongside it.</summary>
public sealed record CompletionItem(string Text, CompletionKind Kind, string? Detail);

/// <summary><see cref="SqlCompletionProvider.Suggest"/>'s result: where the replacement starts
/// (caret minus the word already typed) and the ranked items to offer.</summary>
public sealed record CompletionResult(int ReplaceStart, IReadOnlyList<CompletionItem> Items);

/// <summary>
/// Schema-aware SQL completion driven by the schema snapshot and a scope model (<see cref="SqlScopeAnalyzer"/>,
/// SE-149): "alias." suggests that source's columns — resolved through CTEs and derived tables — a FROM/JOIN
/// position suggests tables/views plus in-scope CTE names, and a SELECT/WHERE/ON/GROUP/ORDER position suggests
/// the columns of the sources visible in that query scope (never leaking across statement boundaries), with a
/// broad tables+columns+keywords mix as the fallback when nothing narrower resolves. Ranking reuses
/// <see cref="SchemaSearch"/> so results order the same way quick-open does.
/// </summary>
public static class SqlCompletionProvider
{
    private const int MaxItems = 200;

    public static CompletionResult Suggest(
        string sql, int caret, SchemaSnapshot snapshot, IReadOnlySet<string> keywords,
        IReadOnlyList<SqlFunction>? functions = null)
    {
        caret = Math.Clamp(caret, 0, sql.Length);
        var (start, fragment, qualifier) = SplitWord(sql, caret);
        var scope = SqlScopeAnalyzer.Analyze(sql, caret);
        functions ??= [];

        var items = scope.Clause switch
        {
            // In FROM/JOIN a dot qualifies a relation ("[dbo].") — offer that schema's tables/views, and nothing
            // when the schema is unknown. Never ColumnsForAlias here: its unknown-alias "show every column"
            // fallback is for a mistyped alias in an expression, and a schema name would always trip it.
            SqlClause.From => qualifier is not null
                ? TablesInSchema(qualifier, fragment, snapshot)
                : TablesAndCtes(fragment, snapshot, scope),
            _ when qualifier is not null => ColumnsForAlias(qualifier, fragment, scope, snapshot),
            SqlClause.On => OnClause(fragment, scope, snapshot, keywords, functions),
            SqlClause.Select or SqlClause.Where
                or SqlClause.GroupBy or SqlClause.Having or SqlClause.OrderBy
                => ScopedColumns(fragment, scope, snapshot, keywords, functions),
            _ => Broad(fragment, snapshot, keywords, functions)
        };

        return new CompletionResult(start, items.Take(MaxItems).ToList());
    }

    // The identifier fragment being typed at the caret, plus the identifier qualifying it before a "." if there
    // is one (e.g. caret after "u.na" in "u.name" → fragment "na", qualifier "u"). What counts as that
    // qualifier is the tokenizer's business, so "[dbo]." and `dbo`. read the same as dbo. does.
    private static (int Start, string Fragment, string? Qualifier) SplitWord(string sql, int caret)
    {
        var start = caret;
        while (start > 0 && IsWordChar(sql[start - 1]))
        {
            start--;
        }

        var qualifier = start > 0 && sql[start - 1] == '.'
            ? SqlScopeAnalyzer.IdentifierBefore(sql, start - 1)
            : null;

        return (start, sql[start..caret], qualifier);
    }

    // ---- clause behaviours ---------------------------------------------------------------------------

    // FROM/JOIN position: the in-scope CTE names first (they're local and few), then the schema's tables/views.
    private static IReadOnlyList<CompletionItem> TablesAndCtes(string fragment, SchemaSnapshot snapshot, SqlScope scope)
    {
        var ctes = RankBy(scope.CteNames, n => n, fragment)
            .Select(n => new CompletionItem(n, CompletionKind.Table, "cte"));

        return ctes.Concat(Tables(fragment, snapshot)).ToList();
    }

    // SELECT-list / WHERE / ON / GROUP BY / ORDER BY / HAVING: the columns of every source visible in the scope
    // (alias-qualified in the detail), plus keywords. Falls back to the broad mix when no source resolves — an
    // incomplete query, or one whose tables aren't in the snapshot — so the box still offers something.
    private static IReadOnlyList<CompletionItem> ScopedColumns(
        string fragment, SqlScope scope, SchemaSnapshot snapshot,
        IReadOnlySet<string> keywords, IReadOnlyList<SqlFunction> functions)
    {
        var columns = scope.Sources
            .SelectMany(s => ResolveColumns(s, snapshot))
            .ToList();

        if (columns.Count == 0)
        {
            return Broad(fragment, snapshot, keywords, functions);
        }

        var ranked = RankBy(columns, c => c.Text, fragment).Take(BroadCategoryCap);
        return ranked.Concat(Functions(fragment, functions)).Concat(Keywords(fragment, keywords, functions)).ToList();
    }

    // A JOIN's ON clause: lead with FK-derived join-condition hints between the just-joined table and the other
    // in-scope sources, then the ordinary scoped columns/keywords so the user can still hand-write a predicate.
    private static IReadOnlyList<CompletionItem> OnClause(
        string fragment, SqlScope scope, SchemaSnapshot snapshot,
        IReadOnlySet<string> keywords, IReadOnlyList<SqlFunction> functions)
    {
        var hints = RankBy(JoinHints(scope, snapshot), h => h.Text, fragment);
        return hints.Concat(ScopedColumns(fragment, scope, snapshot, keywords, functions)).ToList();
    }

    // Full join predicates ("o.user_id = u.id") inferred from foreign keys between the most-recently-joined
    // source and each earlier in-scope source, in both FK directions. Empty unless at least two base-table
    // sources are in scope and an FK actually links them.
    private static IReadOnlyList<CompletionItem> JoinHints(SqlScope scope, SchemaSnapshot snapshot)
    {
        if (scope.Sources.Count < 2)
        {
            return [];
        }

        var target = scope.Sources[^1];
        var targetObj = ResolveObject(target, snapshot);
        var hints = new List<CompletionItem>();

        foreach (var other in scope.Sources.Take(scope.Sources.Count - 1))
        {
            var otherObj = ResolveObject(other, snapshot);

            // target references other: target.col = other.refcol
            if (targetObj is not null)
            {
                foreach (var fk in targetObj.ForeignKeys.Where(fk => Same(fk.ReferencedTable, other.Table)))
                {
                    hints.Add(JoinItem(target.Alias, fk.Column, other.Alias, fk.ReferencedColumn, targetObj.Name, other.Table));
                }
            }

            // other references target: other.col = target.refcol
            if (otherObj is not null)
            {
                foreach (var fk in otherObj.ForeignKeys.Where(fk => Same(fk.ReferencedTable, target.Table)))
                {
                    hints.Add(JoinItem(other.Alias, fk.Column, target.Alias, fk.ReferencedColumn, otherObj.Name, target.Table));
                }
            }
        }

        return hints;
    }

    private static CompletionItem JoinItem(string leftAlias, string leftColumn, string rightAlias, string rightColumn, string fromTable, string? toTable) =>
        new($"{leftAlias}.{leftColumn} = {rightAlias}.{rightColumn}", CompletionKind.Join, $"{fromTable} → {toTable}");

    private static SchemaObject? ResolveObject(SqlScopeSource source, SchemaSnapshot snapshot) =>
        source.Table is { } table
            ? snapshot.Objects.FirstOrDefault(o => o.Name.Equals(table, StringComparison.OrdinalIgnoreCase))
            : null;

    private static bool Same(string a, string? b) => b is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    // Columns of the aliased source when the alias resolves in scope (a base table, or a CTE/derived table with
    // known columns); otherwise (unknown alias, or a CTE/derived whose columns can't be inferred) fall back to
    // every column so the box still offers something, distinguishing them by owning table in Detail.
    private static IReadOnlyList<CompletionItem> ColumnsForAlias(
        string alias, string fragment, SqlScope scope, SchemaSnapshot snapshot)
    {
        var source = scope.Sources.FirstOrDefault(s => s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));

        var candidates = source is not null
            ? ResolveColumns(source, snapshot)
            : [];

        if (candidates.Count == 0)
        {
            candidates = AllColumns(snapshot);
        }

        return RankBy(candidates, c => c.Text, fragment).ToList();
    }

    // Resolve one scope source to its column completion items: a base table's columns from the snapshot (typed),
    // or a CTE/derived table's inferred columns (detail = the source alias). Empty when it can't be resolved
    // (base table absent from the snapshot, or inferred columns unknown) — the caller then decides the fallback.
    private static IReadOnlyList<CompletionItem> ResolveColumns(SqlScopeSource source, SchemaSnapshot snapshot)
    {
        if (source.Table is { } table)
        {
            var obj = snapshot.Objects.FirstOrDefault(o => o.Name.Equals(table, StringComparison.OrdinalIgnoreCase));
            return obj is null
                ? []
                : obj.Columns.Select(c => new CompletionItem(c.Name, CompletionKind.Column, c.Type ?? source.Alias)).ToList();
        }

        return source.Columns is { } cols
            ? cols.Select(name => new CompletionItem(name, CompletionKind.Column, source.Alias)).ToList()
            : [];
    }

    private static IReadOnlyList<CompletionItem> AllColumns(SchemaSnapshot snapshot) =>
        snapshot.Objects
            .SelectMany(o => o.Columns.Select(c => new CompletionItem(c.Name, CompletionKind.Column, c.Type ?? o.QualifiedName)))
            .ToList();

    // FROM/JOIN right after a schema qualifier: only that schema's relations, ranked on the bare name and
    // inserted as the bare name — the schema is already typed, so "[dbo]." + "dbo.users" would double it.
    private static IReadOnlyList<CompletionItem> TablesInSchema(string schema, string fragment, SchemaSnapshot snapshot) =>
        RankBy(snapshot.Objects.Where(o => schema.Equals(o.Schema, StringComparison.OrdinalIgnoreCase)), o => o.Name, fragment)
            .Select(o => new CompletionItem(o.Name, CompletionKind.Table, o.Kind == DbNodeKind.View ? "view" : "table"))
            .ToList();

    private static IReadOnlyList<CompletionItem> Tables(string fragment, SchemaSnapshot snapshot) =>
        RankBy(snapshot.Objects, o => o.QualifiedName, fragment)
            .Select(o => new CompletionItem(o.QualifiedName, CompletionKind.Table, o.Kind == DbNodeKind.View ? "view" : "table"))
            .ToList();

    // Each category is capped BEFORE concatenating, not after: a schema with hundreds of tables/columns
    // would otherwise fill Suggest's overall MaxItems on its own and starve keywords out of the list
    // entirely (they're always small in number, so this cap essentially never trims them).
    private const int BroadCategoryCap = 60;

    private static IReadOnlyList<CompletionItem> Broad(
        string fragment, SchemaSnapshot snapshot, IReadOnlySet<string> keywords, IReadOnlyList<SqlFunction> functions)
    {
        var tables = RankBy(snapshot.Objects, o => o.QualifiedName, fragment)
            .Select(o => new CompletionItem(o.QualifiedName, CompletionKind.Table, o.Kind == DbNodeKind.View ? "view" : "table"))
            .Take(BroadCategoryCap);

        var columns = RankBy(AllColumns(snapshot), c => c.Text, fragment).Take(BroadCategoryCap);

        return tables.Concat(columns).Concat(Functions(fragment, functions)).Concat(Keywords(fragment, keywords, functions)).ToList();
    }

    // Function catalogue entries for an expression position: name inserted, signature shown as the detail.
    private static IEnumerable<CompletionItem> Functions(string fragment, IReadOnlyList<SqlFunction> functions) =>
        RankBy(functions, f => f.Name, fragment)
            .Select(f => new CompletionItem(f.Name, CompletionKind.Function, f.Signature))
            .Take(BroadCategoryCap);

    // Keyword entries, minus any that a function already covers (e.g. COUNT/SUM/AVG are keywords AND functions):
    // the function entry carries a signature and is the more useful of the two, so the bare keyword is dropped.
    private static IEnumerable<CompletionItem> Keywords(
        string fragment, IReadOnlySet<string> keywords, IReadOnlyList<SqlFunction> functions)
    {
        var functionNames = new HashSet<string>(functions.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        return RankBy(keywords.Where(k => !functionNames.Contains(k)), k => k, fragment)
            .Select(k => new CompletionItem(k, CompletionKind.Keyword, "keyword"))
            .Take(BroadCategoryCap);
    }

    // Fragment-ranked subset via the same TryRank order quick-open uses; an empty fragment
    // (Ctrl+Space with nothing typed yet) keeps every candidate, capped later by Suggest.
    private static IEnumerable<T> RankBy<T>(IEnumerable<T> items, Func<T, string> text, string fragment)
    {
        if (fragment.Length == 0)
        {
            return items;
        }

        return items
            .Select(item => (Item: item, Matched: SchemaSearch.TryRank(text(item), fragment, out var rank), Rank: rank))
            .Where(t => t.Matched)
            .OrderBy(t => t.Rank)
            .ThenBy(t => text(t.Item), StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Item);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
