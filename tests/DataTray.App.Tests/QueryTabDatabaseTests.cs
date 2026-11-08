namespace DataTray.App.Tests;

public class QueryTabDatabaseTests
{
    [Fact] // The database a tab was opened on (SE-267: the tree node's) is what the picker lands on, not
           // the connection's configured default.
    public async Task Database_picker_selects_the_tab_database_when_the_list_arrives()
    {
        var document = QueryDocumentFixture.NewDocument(out var connections, QueryDocumentFixture.Ready("master", "Sales"));
        var prod = connections.List().First(c => c.Id == "c1");

        document.InitQuery(prod, "Sales");
        await QueryDocumentFixture.WaitFor(() => document.SelectedDatabase is not null);

        Assert.Equal("Sales", document.SelectedDatabase);
        Assert.Equal(["master", "Sales"], document.AvailableDatabases);
    }

    [Fact] // SE-267: the tab must keep running against its database while the next connection's database
           // list is still loading — clearing the picker used to null the target out, so a query run in
           // that window (or on a provider whose listing never answers) went to the connection's default.
    public async Task Switching_connection_keeps_the_tab_database_while_the_new_list_loads()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        var document = QueryDocumentFixture.NewDocument(out var connections, QueryDocumentFixture.Ready("master", "Sales"), pending.Task);
        var prod = connections.List().First(c => c.Id == "c1");
        var staging = connections.List().First(c => c.Id == "c2");

        document.InitQuery(prod, "Sales");
        await QueryDocumentFixture.WaitFor(() => document.SelectedDatabase is not null);

        document.Connection = staging;

        Assert.Equal("Staging · Sales", document.TabTooltip);   // was "Staging · staging" before the fix
    }
}
