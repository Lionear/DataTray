using DataTray.Core.Editing;
using DataTray.Sdk;
using DataTray.Sdk.Query;

namespace DataTray.Core.Tests.Editing;

/// <summary>
/// SE-280: a filtered/partial unique index (e.g. a soft-delete table's
/// "UNIQUE (OrgId, ItemId) WHERE IsDeleted = 0") is only unique among the rows its filter covers, so the
/// key columns alone can still match more than one row outside it (a duplicate soft-deleted row sharing
/// the same key values). <see cref="QueryResult.EditFilterPredicate"/> carries the filter through so
/// <see cref="CrudStatementBuilder"/> ANDs it into every generated WHERE.
/// </summary>
public class EditFilterPredicateTests
{
    [Fact]
    public void Update_WithEditFilterPredicate_AndsItIntoTheWhereClause()
    {
        var set = NewCompositeKeyRow(editFilterPredicate: "[IsDeleted]=(0)");
        set.Rows[0][2] = "changed";

        var statement = Assert.Single(CrudStatementBuilder.Build(set, new TestDialect()));

        Assert.Equal(
            "UPDATE \"t\" SET \"label\" = @p0 WHERE \"org_id\" = @p1 AND \"item_id\" = @p2 AND ([IsDeleted]=(0))",
            statement.Text);
    }

    [Fact]
    public void Update_WithoutEditFilterPredicate_LeavesTheWhereClauseAsIs()
    {
        var set = NewCompositeKeyRow(editFilterPredicate: null);
        set.Rows[0][2] = "changed";

        var statement = Assert.Single(CrudStatementBuilder.Build(set, new TestDialect()));

        Assert.Equal(
            "UPDATE \"t\" SET \"label\" = @p0 WHERE \"org_id\" = @p1 AND \"item_id\" = @p2",
            statement.Text);
    }

    [Fact]
    public void Delete_WithEditFilterPredicate_AndsItIntoTheWhereClause()
    {
        var set = NewCompositeKeyRow(editFilterPredicate: "[IsDeleted]=(0)");
        set.Rows[0].MarkDeleted();

        var statement = Assert.Single(CrudStatementBuilder.Build(set, new TestDialect()));

        Assert.Equal(
            "DELETE FROM \"t\" WHERE \"org_id\" = @p0 AND \"item_id\" = @p1 AND ([IsDeleted]=(0))",
            statement.Text);
    }

    private static EditableResultSet NewCompositeKeyRow(string? editFilterPredicate)
    {
        var result = new QueryResult
        {
            Columns =
            [
                new ResultColumn("org_id", typeof(int)) { IsKey = true, BaseTable = "t", BaseColumn = "org_id" },
                new ResultColumn("item_id", typeof(int)) { IsKey = true, BaseTable = "t", BaseColumn = "item_id" },
                new ResultColumn("label", typeof(string)) { BaseTable = "t", BaseColumn = "label" }
            ],
            Rows = [[1, 2, "original"]],
            EditFilterPredicate = editFilterPredicate
        };

        return EditableResultSet.From(result);
    }

    private sealed class TestDialect : ISqlDialect
    {
        public IReadOnlySet<string> Keywords => new HashSet<string>();

        public string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

        public string QualifyName(string? database, string? schema, string table) => QuoteIdentifier(table);

        public string Paginate(string sql, int limit, int offset, string? orderBy = null) => sql;
    }
}
