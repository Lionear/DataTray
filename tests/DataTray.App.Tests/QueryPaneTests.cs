using Avalonia.Controls;
using DataTray.App.ViewModels;
using DataTray.Core.Logging;
using DataTray.Core.Plugins;
using DataTray.Sdk.Extensibility;
using DataTray.Sdk.Localization;

namespace DataTray.App.Tests;

public class QueryPaneTests : IClassFixture<HeadlessDrawing>
{
    [Fact]
    public void TogglePane_Opens_CallsCreatePaneOnce_AndClosingDisposes()
    {
        var (document, slot, plugin) = OpenablePane();

        document.TogglePaneCommand.Execute(slot);
        var control = Assert.IsType<PaneControl>(slot.Control);
        document.TogglePaneCommand.Execute(slot);

        Assert.Equal(1, plugin.CreateCalls);
        Assert.True(control.Disposed);
        Assert.Null(slot.Control);
    }

    [Fact]
    public void TogglePane_CreatePaneThrows_SlotShowsError_TabStillRuns()
    {
        var (document, slot, _) = OpenablePane(() => throw new InvalidOperationException("boom"));
        var reported = new List<(OutputLevel, string)>();
        document.Reported += (level, message) => reported.Add((level, message));

        document.TogglePaneCommand.Execute(slot);

        Assert.Equal("boom", slot.Error);
        Assert.Null(slot.Control);
        Assert.Equal([(OutputLevel.Error, "Builder: boom")], reported);
    }

    [Fact]
    public void Link_MakesSqlReadOnly_EditAsSql_UnlinksAndRaisesUnlinked()
    {
        var document = QueryDocument();
        IQueryDocument query = document;
        var unlinked = 0;
        query.Unlinked += (_, _) => unlinked++;

        query.Link("Builder");
        var linkedWhileOwned = (document.IsSqlLinked, document.LinkOwner, query.IsLinked);
        document.EditAsSqlCommand.Execute(null);
        document.EditAsSqlCommand.Execute(null);

        Assert.Equal((true, "Builder", true), linkedWhileOwned);
        Assert.Equal((false, null, false), (document.IsSqlLinked, document.LinkOwner, query.IsLinked));
        Assert.Equal(1, unlinked);
    }

    [Fact]
    public void SetSql_RaisesSqlChanged_UserTyping_RaisesSqlChanged()
    {
        var document = QueryDocument();
        IQueryDocument query = document;
        var changes = 0;
        query.SqlChanged += (_, _) => changes++;

        query.SetSql("select 1");
        document.Sql = "select 2";   // what the editor's two-way sync does while the user types
        document.Sql = "select 2";

        Assert.Equal(2, changes);
    }

    [Fact] // SE-267: the database survives the list reload, so the event waits for the list and never sees null.
    public async Task ContextChanged_FiresOnceAfterDatabaseListSettles()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        var document = QueryDocumentFixture.NewDocument(out var connections, QueryDocumentFixture.Ready("master", "Sales"), pending.Task);
        IQueryDocument query = document;
        var seen = new List<string?>();
        query.ContextChanged += (_, _) => seen.Add(query.Database);
        document.InitQuery(connections.List().First(c => c.Id == "c1"));
        await QueryDocumentFixture.WaitFor(() => seen.Count > 0);

        document.Connection = connections.List().First(c => c.Id == "c2");
        var whileLoading = seen.Count;
        pending.SetResult(["staging"]);
        await QueryDocumentFixture.WaitFor(() => seen.Count > 1);

        Assert.Equal(1, whileLoading);
        Assert.Equal(["master", "master"], seen);
    }

    [Fact]
    public void ContextChanged_PaneNoLongerApplies_ClosesAndStaysClosed()
    {
        var (document, slot, plugin) = OpenablePane();
        document.TogglePaneCommand.Execute(slot);
        var control = Assert.IsType<PaneControl>(slot.Control);
        plugin.Applies = false;

        document.SelectedDatabase = "Sales";
        document.TogglePaneCommand.Execute(slot);

        Assert.True(control.Disposed);
        Assert.Equal((false, false, 1), (slot.IsOpen, slot.IsAvailable, plugin.CreateCalls));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsReadOnly_FollowsSavedConnectionReadOnly(bool readOnly)
    {
        var document = QueryDocumentFixture.NewDocument(out var connections, QueryDocumentFixture.Ready("master"));
        var connection = connections.Save("c3", "Guarded", "fake", new Dictionary<string, string?>(), readOnly: readOnly);

        document.InitQuery(connection);

        Assert.Equal(readOnly, ((IQueryDocument)document).IsReadOnly);
    }

    [Fact]
    public void PaneState_RoundTrips()
    {
        var (first, firstSlot, firstPlugin) = OpenablePane();
        first.TogglePaneCommand.Execute(firstSlot);
        Assert.IsAssignableFrom<IQueryPaneContext>(firstPlugin.LastContext).State = "{\"tables\":[\"Orders\"]}";
        var (second, secondSlot, secondPlugin) = OpenablePane();

        second.RestorePaneStates(first.PaneStates);
        second.TogglePaneCommand.Execute(secondSlot);

        Assert.Equal("{\"tables\":[\"Orders\"]}", Assert.IsAssignableFrom<IQueryPaneContext>(secondPlugin.LastContext).State);
    }

    [Fact]
    public void DisposePanes_DisposesOpenPanes_AndDetachesEvents()
    {
        var (document, slot, plugin) = OpenablePane();
        document.TogglePaneCommand.Execute(slot);
        var control = Assert.IsType<PaneControl>(slot.Control);
        var changes = 0;
        Assert.IsAssignableFrom<IQueryPaneContext>(plugin.LastContext).Document.SqlChanged += (_, _) => changes++;

        document.DisposePanes();
        document.Sql = "select 1";

        Assert.True(control.Disposed);
        Assert.Null(slot.Control);
        Assert.Equal(0, changes);
    }

    private static DocumentViewModel QueryDocument()
    {
        var document = QueryDocumentFixture.NewDocument(out var connections, QueryDocumentFixture.Ready("master", "Sales"));
        document.InitQuery(connections.List().First(c => c.Id == "c1"));
        return document;
    }

    // A mounted query tab with one applicable pane; the default factory builds a disposable control.
    private static (DocumentViewModel Document, QueryPaneSlot Slot, FakePanePlugin Plugin) OpenablePane(Func<Control>? create = null)
    {
        var document = QueryDocument();
        var plugin = new FakePanePlugin(create ?? (() => new PaneControl()));
        var pane = new SubsystemQueryPane("builder", "Builder", new HashSet<string>(), new FakePluginLocalizer(), plugin);
        document.MountQueryPanes([pane], new FakeHostUi(), _ => null);
        return (document, document.QueryPanes[0], plugin);
    }

    private sealed class FakePanePlugin(Func<Control> create) : IQueryPanePlugin
    {
        public int CreateCalls { get; private set; }
        public IQueryPaneContext? LastContext { get; private set; }
        public string PaneId => "builder";
        public string Title => "Builder";
        public bool Applies { get; set; } = true;
        public bool AppliesTo(IQueryDocument document) => Applies;

        public Control CreatePane(IQueryPaneContext context)
        {
            CreateCalls++;
            LastContext = context;
            return create();
        }
    }

    private sealed class PaneControl : Control, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeHostUi : IHostUi
    {
        public Task ShowDialogAsync(string title, Control content) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
    }

    private sealed class FakePluginLocalizer : IPluginLocalizer
    {
        public string this[string key] => key;
        public bool Contains(string key) => false;
        public string Get(string key, params object[] args) => key;
    }
}
