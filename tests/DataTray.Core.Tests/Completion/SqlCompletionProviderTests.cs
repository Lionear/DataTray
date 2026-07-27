using System.Collections.Generic;
using System.Linq;
using DataTray.Core.Completion;
using DataTray.Core.Schema;
using DataTray.Sdk;
using DataTray.Sdk.Schema;

namespace DataTray.Core.Tests.Completion;

public class SqlCompletionProviderTests
{
    private static readonly SchemaSnapshot Schema = new(
    [
        new SchemaObject
        {
            Kind = DbNodeKind.Table, Name = "users",
            Columns = [new("id", "int"), new("name", "text"), new("email", "text")]
        },
        new SchemaObject
        {
            Kind = DbNodeKind.Table, Name = "orders",
            Columns = [new("id", "int"), new("user_id", "int"), new("total", "numeric")],
            ForeignKeys = [new("user_id", "users", "id")]
        }
    ]);

    private static readonly IReadOnlySet<string> Keywords =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "SELECT", "FROM", "WHERE", "JOIN", "GROUP", "ORDER" };

    private static readonly IReadOnlyList<SqlFunction> Funcs =
    [
        new("coalesce", "coalesce(value [, ...])"),
        new("now", "now()"),
        new("count", "count(* | expression)")
    ];

    private static CompletionResult At(string queryWithCaret)
    {
        var caret = queryWithCaret.IndexOf('|');
        var sql = queryWithCaret.Remove(caret, 1);
        return SqlCompletionProvider.Suggest(sql, caret, Schema, Keywords, Funcs);
    }

    private static IReadOnlyList<string> Texts(CompletionResult r) => r.Items.Select(i => i.Text).ToList();

    [Fact]
    public void Alias_dot_suggests_that_tables_columns()
    {
        var result = At("SELECT u.| FROM users u");

        Assert.All(result.Items, i => Assert.Equal(CompletionKind.Column, i.Kind));
        Assert.Equal(["email", "id", "name"], Texts(result).OrderBy(x => x));
        Assert.DoesNotContain("total", Texts(result)); // orders' column must not leak in
    }

    [Fact]
    public void Alias_dot_resolves_through_a_cte()
    {
        var result = At("WITH c AS (SELECT a, b FROM users) SELECT x.| FROM c x");

        Assert.Equal(["a", "b"], Texts(result).OrderBy(x => x));
    }

    [Fact] // An unknown alias falls back to every column rather than showing nothing.
    public void Unknown_alias_falls_back_to_all_columns()
    {
        var result = At("SELECT z.| FROM users u");

        Assert.Contains("name", Texts(result));
        Assert.Contains("total", Texts(result));
    }

    [Fact]
    public void From_position_suggests_tables()
    {
        var result = At("SELECT * FROM |");

        Assert.Contains("users", Texts(result));
        Assert.Contains("orders", Texts(result));
        Assert.All(result.Items, i => Assert.Equal(CompletionKind.Table, i.Kind));
    }

    [Fact]
    public void From_position_offers_cte_names_tagged_as_cte()
    {
        var result = At("WITH recent AS (SELECT id FROM orders) SELECT * FROM re|");

        var cte = Assert.Single(result.Items, i => i.Text == "recent");
        Assert.Equal("cte", cte.Detail);
    }

    [Fact]
    public void Select_list_suggests_in_scope_columns_and_keywords()
    {
        var result = At("SELECT | FROM users u");
        var texts = Texts(result);

        Assert.Contains("name", texts);   // users' columns are in scope
        Assert.Contains("email", texts);
        Assert.DoesNotContain("total", texts); // orders isn't in this query
        Assert.Contains(result.Items, i => i.Kind == CompletionKind.Keyword);
    }

    [Fact]
    public void Where_clause_scopes_columns_to_the_joined_sources()
    {
        var result = At("SELECT * FROM users u JOIN orders o ON u.id = o.user_id WHERE |");
        var texts = Texts(result);

        Assert.Contains("email", texts);   // from users
        Assert.Contains("total", texts);    // from orders
    }

    [Fact]
    public void Does_not_leak_columns_across_statement_boundaries()
    {
        // Caret is in the second statement (orders only); users' columns must not appear as scoped columns.
        var result = At("SELECT * FROM users u; SELECT o.| FROM orders o");

        Assert.Equal(["id", "total", "user_id"], Texts(result).OrderBy(x => x));
    }

    [Fact] // SE-149 phase 2: functions appear in expression positions, tagged Function with their signature.
    public void Select_list_offers_functions_with_their_signature()
    {
        var result = At("SELECT coa| FROM users u");

        var fn = Assert.Single(result.Items, i => i.Kind == CompletionKind.Function);
        Assert.Equal("coalesce", fn.Text);
        Assert.Equal("coalesce(value [, ...])", fn.Detail);
    }

    [Fact]
    public void Where_clause_offers_functions()
    {
        var result = At("SELECT * FROM users u WHERE no|");
        Assert.Contains(result.Items, i => i is { Kind: CompletionKind.Function, Text: "now" });
    }

    [Fact] // A FROM position is for relations only — functions must not appear there.
    public void From_position_offers_no_functions()
    {
        var result = At("SELECT * FROM |");
        Assert.DoesNotContain(result.Items, i => i.Kind == CompletionKind.Function);
    }

    [Fact] // After "alias." only that source's columns are offered, never functions.
    public void Alias_dot_offers_no_functions()
    {
        var result = At("SELECT u.| FROM users u");
        Assert.DoesNotContain(result.Items, i => i.Kind == CompletionKind.Function);
    }

    [Fact] // COUNT is both a keyword and a function — it should appear once, as the function (with a signature).
    public void A_name_that_is_both_keyword_and_function_is_not_duplicated()
    {
        var keywords = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "SELECT", "COUNT" };
        var functions = new List<SqlFunction> { new("count", "count(* | expression)") };
        const string sql = "SELECT cou FROM users u";

        var result = SqlCompletionProvider.Suggest(sql, "SELECT cou".Length, Schema, keywords, functions);

        var counts = result.Items.Where(i => i.Text.Equals("count", System.StringComparison.OrdinalIgnoreCase)).ToList();
        var single = Assert.Single(counts);
        Assert.Equal(CompletionKind.Function, single.Kind);
    }

    // ---- SE-149 phase 3: FK-aware JOIN hints -------------------------------------------------------

    [Fact] // ON position leads with the FK-derived join predicate, as a high-priority Join item.
    public void On_clause_suggests_the_fk_join_condition()
    {
        var result = At("SELECT * FROM users u JOIN orders o ON |");

        var join = result.Items.First();
        Assert.Equal(CompletionKind.Join, join.Kind);
        Assert.Equal("o.user_id = u.id", join.Text);
        Assert.Equal("orders → users", join.Detail);
    }

    [Fact] // The FK is found regardless of which side is joined second (both directions considered).
    public void On_clause_finds_the_fk_when_the_referenced_table_is_joined_second()
    {
        var result = At("SELECT * FROM orders o JOIN users u ON |");

        Assert.Contains(result.Items, i => i is { Kind: CompletionKind.Join, Text: "o.user_id = u.id" });
    }

    [Fact] // Unaliased sources use their own names in the predicate.
    public void Join_hint_uses_table_names_when_unaliased()
    {
        var result = At("SELECT * FROM users JOIN orders ON |");
        Assert.Contains(result.Items, i => i is { Kind: CompletionKind.Join, Text: "orders.user_id = users.id" });
    }

    [Fact] // No FK between the joined tables → no join hint (but columns are still offered).
    public void No_join_hint_without_a_foreign_key()
    {
        var result = At("SELECT * FROM users u JOIN users u2 ON |");

        Assert.DoesNotContain(result.Items, i => i.Kind == CompletionKind.Join);
        Assert.Contains(result.Items, i => i.Kind == CompletionKind.Column);
    }

    [Fact]
    public void Replace_start_backs_up_over_the_typed_fragment()
    {
        var result = At("SELECT * FROM us|");
        Assert.Equal("SELECT * FROM ".Length, result.ReplaceStart);
    }
}
