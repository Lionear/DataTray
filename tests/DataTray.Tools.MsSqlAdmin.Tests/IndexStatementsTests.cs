using DataTray.Sdk;
using DataTray.Tools.MsSqlAdmin;

namespace DataTray.Tools.MsSqlAdmin.Tests;

/// <summary>
/// The statements behind the Indexes-node actions. They are short enough to read and wrong in ways that are
/// expensive: a rebuild aimed at the wrong table is an outage, and "ALL" quoted into an identifier is an
/// index nobody has.
/// </summary>
public class IndexStatementsTests
{
    private static readonly ISqlDialect Dialect = new BracketDialect();

    [Fact]
    public void Rebuild_of_one_index_names_the_index_and_its_table()
    {
        var sql = IndexStatements.Build(Dialect, IndexAction.Rebuild, "dbo", "Fitting", "IX_Fitting_Name");

        Assert.Equal("ALTER INDEX [IX_Fitting_Name] ON [dbo].[Fitting] REBUILD", sql);
    }

    [Fact]
    public void Rebuild_of_the_folder_uses_the_ALL_keyword_unquoted()
    {
        // Quoting it would name an index called "ALL" rather than mean every index.
        var sql = IndexStatements.Build(Dialect, IndexAction.Rebuild, "dbo", "Fitting", index: null);

        Assert.Equal("ALTER INDEX ALL ON [dbo].[Fitting] REBUILD", sql);
    }

    [Theory]
    [InlineData(IndexAction.Reorganize, "REORGANIZE")]
    [InlineData(IndexAction.Disable, "DISABLE")]
    public void Each_action_runs_its_own_verb(IndexAction action, string verb)
    {
        var sql = IndexStatements.Build(Dialect, action, "dbo", "Fitting", "IX_Fitting_Name");

        Assert.EndsWith(verb, sql);
    }

    [Fact]
    public void Drop_is_its_own_statement_not_an_ALTER()
    {
        var sql = IndexStatements.Build(Dialect, IndexAction.Drop, "dbo", "Fitting", "IX_Fitting_Name");

        Assert.Equal("DROP INDEX [IX_Fitting_Name] ON [dbo].[Fitting]", sql);
    }

    [Fact]
    public void Drop_refuses_to_mean_every_index()
    {
        // There is no DROP INDEX ALL, and quietly looping over a table's indexes behind one menu click is
        // not something a Drop action should invent.
        Assert.Throws<InvalidOperationException>(
            () => IndexStatements.Build(Dialect, IndexAction.Drop, "dbo", "Fitting", index: null));
    }

    [Fact]
    public void A_table_without_a_schema_is_left_unqualified()
    {
        var sql = IndexStatements.Build(Dialect, IndexAction.Rebuild, schema: null, "Fitting", "IX_Fitting_Name");

        Assert.Equal("ALTER INDEX [IX_Fitting_Name] ON [Fitting] REBUILD", sql);
    }

    [Fact]
    public void The_constraint_check_quotes_the_name_inside_the_literal()
    {
        // OBJECT_ID reads its argument as text, so a table called "my.table" must arrive bracketed or it
        // resolves as schema "my", table "table" — a different object, quite possibly an existing one.
        var sql = IndexStatements.ConstraintCheck(Dialect, "dbo", "my.table", "IX_A");

        Assert.Contains("OBJECT_ID(N'[dbo].[my.table]')", sql);
        Assert.Contains("i.name = N'IX_A'", sql);
    }

    [Fact]
    public void An_apostrophe_in_a_name_cannot_end_the_literal()
    {
        var sql = IndexStatements.ConstraintCheck(Dialect, "dbo", "O'Brien", "IX_A");

        Assert.Contains("N'[dbo].[O''Brien]'", sql);
    }

    /// <summary>SQL Server's quoting, standing in for the real dialect so these stay host-free. The rest of
    /// the dialect is irrelevant here — these statements never page and never qualify across databases.</summary>
    private sealed class BracketDialect : ISqlDialect
    {
        public IReadOnlySet<string> Keywords { get; } = new HashSet<string>();

        public string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";

        public string QualifyName(string? database, string? schema, string table) => QuoteIdentifier(table);

        public string Paginate(string sql, int limit, int offset, string? orderBy = null) => sql;
    }
}
