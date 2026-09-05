using DataTray.App.ViewModels;

namespace DataTray.App.Tests;

public class AlterObjectLabelTests
{
    [Fact] // SE-270: a DROP is confirmed against this label, and "DROP SCHEMA [X]" names no database of
           // its own — so the label has to say which catalog the statement lands in.
    public void Drop_label_names_the_database_and_schema_it_will_run_against()
    {
        // Drop Schema: the schema is the target, its database is the context that was missing.
        Assert.Equal("rick-test › DataSync", AlterObjectDialogViewModel.QualifiedLabel("rick-test", null, "DataSync"));

        // Drop Table: both levels present.
        Assert.Equal("rick-test › dbo › Orders", AlterObjectDialogViewModel.QualifiedLabel("rick-test", "dbo", "Orders"));

        // Schema-less engine (SQLite), and Drop Database — which passes no database because it runs from
        // the connection's own catalog. Neither may gain a stray separator.
        Assert.Equal("Orders", AlterObjectDialogViewModel.QualifiedLabel(null, null, "Orders"));
        Assert.Equal("rick-test", AlterObjectDialogViewModel.QualifiedLabel("", "", "rick-test"));
    }
}
