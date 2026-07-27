using DataTray.Core.Editing;
using DataTray.Sdk;
using DataTray.Sdk.Query;

namespace DataTray.Core.Tests.Editing;

/// <summary>
/// SE-29: NULL and the empty string are different values, and the grid has to keep them apart — both on
/// screen and on the way back to the database.
/// </summary>
public class NullVersusEmptyStringTests
{
    [Fact]
    public void EditText_EmptyTextOverNullCell_LeavesTheNullAlone()
    {
        var row = EditableRow.Existing([null]);

        // What opening a NULL cell's editor and leaving it looks like from the model's side.
        row.Cells[0].EditText = string.Empty;

        Assert.Null(row[0]);
        Assert.Equal(RowState.Unchanged, row.State);
    }

    [Fact]
    public void EditText_EmptyTextOverValueCell_ClearsToEmptyString()
    {
        var row = EditableRow.Existing(["hello"]);

        row.Cells[0].EditText = string.Empty;

        Assert.Equal(string.Empty, row[0]);
        Assert.Equal(RowState.Modified, row.State);
    }

    [Fact]
    public void EditText_TypedTextOverNullCell_TakesTheValue()
    {
        var row = EditableRow.Existing([null]);

        row.Cells[0].EditText = "x";

        Assert.Equal("x", row[0]);
        Assert.Equal(RowState.Modified, row.State);
    }

    [Fact]
    public void SetEmpty_OnNullCell_WritesAnEmptyString()
    {
        var row = EditableRow.Existing([null]);

        row.Cells[0].SetEmpty();

        Assert.Equal(string.Empty, row[0]);
        Assert.Equal(RowState.Modified, row.State);
    }

    [Fact]
    public void SetNull_OnNewRow_MarksTheCellAsExplicitlyNull()
    {
        var row = EditableRow.New(1);

        row.Cells[0].SetNull();

        Assert.True(row.Cells[0].IsExplicitNull);
    }

    [Fact]
    public void SetNull_ThenTyping_ClearsTheExplicitNullMark()
    {
        var row = EditableRow.New(1);
        row.Cells[0].SetNull();

        row.Cells[0].Value = "x";

        Assert.False(row.Cells[0].IsExplicitNull);
    }

    [Fact]
    public void Insert_UnsetColumn_IsLeftToTheDatabase()
    {
        var set = NewRowWith(cell => { });

        var statement = Assert.Single(CrudStatementBuilder.Build(set, new TestDialect()));

        // Quoted, so the assertion doesn't trip over the table name "notes".
        Assert.DoesNotContain("\"note\"", statement.Text);
    }

    [Fact]
    public void Insert_ExplicitlyNulledColumn_IsWrittenAsNull()
    {
        var set = NewRowWith(cell => cell.SetNull());

        var statement = Assert.Single(CrudStatementBuilder.Build(set, new TestDialect()));

        // The column has to appear, bound to a null parameter — otherwise a DEFAULT on it wins and the
        // row lands with the default instead of the NULL that was asked for.
        Assert.Contains("\"note\"", statement.Text);
        Assert.Contains(statement.Parameters, p => p.Value is null);
    }

    [Fact]
    public void ChangeSet_ExplicitlyNulledColumn_IsIncluded()
    {
        var set = NewRowWith(cell => cell.SetNull());

        var change = Assert.Single(ChangeSetBuilder.Build(set)!.Rows);

        Assert.Contains(change.Cells, c => c.Column == "note" && c.Value is null);
    }

    // An editable one-column-plus-key result with a single added row, with `apply` run on the note cell.
    private static EditableResultSet NewRowWith(Action<EditableCell> apply)
    {
        var result = new QueryResult
        {
            Columns =
            [
                new ResultColumn("id", typeof(int)) { IsKey = true, BaseTable = "notes", BaseColumn = "id" },
                new ResultColumn("note", typeof(string)) { BaseTable = "notes", BaseColumn = "note" }
            ],
            Rows = []
        };

        var set = EditableResultSet.From(result);
        var row = EditableRow.New(2);
        row[0] = 1;
        apply(row.Cells[1]);
        set.Rows.Add(row);
        return set;
    }

    private sealed class TestDialect : ISqlDialect
    {
        public IReadOnlySet<string> Keywords => new HashSet<string>();

        public string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

        public string QualifyName(string? database, string? schema, string table) =>
            schema is null ? QuoteIdentifier(table) : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

        public string Paginate(string sql, int limit, int offset, string? orderBy = null) => sql;
    }
}
