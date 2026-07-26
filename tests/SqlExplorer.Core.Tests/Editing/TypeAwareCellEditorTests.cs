using SqlExplorer.Core.Editing;

namespace SqlExplorer.Core.Tests.Editing;

/// <summary>
/// SE-30 phase 1: the binding targets behind the boolean and date editors. The controls themselves are
/// the view's business; what has to hold here is that NULL stays reachable and nothing is lost on the
/// way through a typed editor.
/// </summary>
public class TypeAwareCellEditorTests
{
    [Fact]
    public void BoolValue_NullCell_ReadsAsNull()
    {
        var row = EditableRow.Existing([null]);

        Assert.Null(row.Cells[0].BoolValue);
    }

    [Theory]
    [InlineData(1L, true)]
    [InlineData(0L, false)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    public void BoolValue_NonBooleanStorage_ReadsAsBool(object stored, bool expected)
    {
        // SQLite has no boolean type and MySQL's is tinyint(1), so the column arrives as a number or text.
        var row = EditableRow.Existing([stored]);

        Assert.Equal(expected, row.Cells[0].BoolValue);
    }

    [Fact]
    public void BoolValue_ClearedToNull_SetsExplicitNull()
    {
        var row = EditableRow.New(1);
        row.Cells[0].BoolValue = true;

        // Clearing a three-state checkbox.
        row.Cells[0].BoolValue = null;

        Assert.Null(row[0]);
        Assert.True(row.Cells[0].IsExplicitNull);
    }

    [Fact]
    public void BoolValue_Set_StoresARealBool()
    {
        var row = EditableRow.Existing(["0"]);

        row.Cells[0].BoolValue = true;

        Assert.Equal(true, row[0]);
        Assert.Equal(RowState.Modified, row.State);
    }

    [Fact]
    public void DateValue_NullCell_ReadsAsNull()
    {
        var row = EditableRow.Existing([null]);

        Assert.Null(row.Cells[0].DateValue);
    }

    [Fact]
    public void DateValue_PickingADate_KeepsTheExistingTimeOfDay()
    {
        var row = EditableRow.Existing([new DateTime(2026, 1, 2, 13, 45, 30)]);

        // A date picker only edits the date half; the time must survive it.
        row.Cells[0].DateValue = new DateTime(2026, 3, 4);

        Assert.Equal(new DateTime(2026, 3, 4, 13, 45, 30), row[0]);
    }

    [Fact]
    public void DateValue_OnANullCell_StartsAtMidnight()
    {
        var row = EditableRow.Existing([null]);

        row.Cells[0].DateValue = new DateTime(2026, 3, 4);

        Assert.Equal(new DateTime(2026, 3, 4), row[0]);
    }

    [Fact]
    public void DateValue_ClearedToNull_SetsExplicitNull()
    {
        var row = EditableRow.New(1);
        row.Cells[0].DateValue = new DateTime(2026, 3, 4);

        row.Cells[0].DateValue = null;

        Assert.Null(row[0]);
        Assert.True(row.Cells[0].IsExplicitNull);
    }

    [Fact]
    public void DateValue_TextStorage_IsParsed()
    {
        // SQLite stores dates as text.
        var row = EditableRow.Existing(["2026-03-04 08:15:00"]);

        Assert.Equal(new DateTime(2026, 3, 4, 8, 15, 0), row.Cells[0].DateValue);
    }
}
