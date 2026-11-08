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
    public void ContextChanged_PaneNoLongerApplies_ClosesAndDisposes()
    {
        var (document, slot, plugin) = OpenablePane();
        document.TogglePaneCommand.Execute(slot);
        var control = Assert.IsType<PaneControl>(slot.Control);
        plugin.Applies = false;

        document.SelectedDatabase = "Sales";

        Assert.True(control.Disposed);
        Assert.Equal((false, false), (slot.IsOpen, slot.IsAvailable));
    }

    [Fact]
    public void TogglePane_NotApplicable_DoesNothing()
    {
        var document = QueryDocument();
        var plugin = new FakePanePlugin(() => new PaneControl()) { Applies = false };
        var slot = Mount(document, plugin);

        document.TogglePaneCommand.Execute(slot);

        Assert.Equal((false, 0), (slot.IsOpen, plugin.CreateCalls));
    }

    [Fact] // Panes belong to query tabs only; a browse tab never offers one, whatever AppliesTo says.
    public void MountQueryPanes_OnBrowseTab_PaneNotAvailable()
    {
        var document = QueryDocumentFixture.NewDocument(out var connections, QueryDocumentFixture.Ready("master"));
        document.InitBrowse(connections.List().First(c => c.Id == "c1"), "master", "dbo", "Orders");

        var slot = Mount(document, new FakePanePlugin(() => new PaneControl()));

        Assert.False(slot.IsAvailable);
    }

    [Fact]
    public void ContextChanged_FiresOnce_OnBrowseInit()
    {
        var document = QueryDocumentFixture.NewDocument(out var connections, QueryDocumentFixture.Ready("master"));
        var changes = 0;
        ((IQueryDocument)document).ContextChanged += (_, _) => changes++;

        document.InitBrowse(connections.List().First(c => c.Id == "c1"), "master", "dbo", "Orders");

        Assert.Equal(1, changes);
    }

    [Fact] // The SQL stays in the editor; only the link goes, so the editor is editable again.
    public void ClosePane_WhileLinked_UnlinksEditor()
    {
        var (document, slot, plugin) = OpenablePane();
        document.TogglePaneCommand.Execute(slot);
        var query = Assert.IsAssignableFrom<IQueryPaneContext>(plugin.LastContext).Document;
        query.SetSql("select * from Orders");
        query.Link("Builder");
        var unlinked = 0;
        query.Unlinked += (_, _) => unlinked++;

        document.TogglePaneCommand.Execute(slot);

        Assert.Equal((false, 1, "select * from Orders"), (document.IsSqlLinked, unlinked, document.Sql));
    }

    [Fact]
    public void ThrowingSqlChangedHandler_IsReported_AndOtherHandlersStillRun()
    {
        var document = QueryDocument();
        IQueryDocument query = document;
        var reported = new List<(OutputLevel, string)>();
        document.Reported += (level, message) => reported.Add((level, message));
        var calls = 0;
        query.SqlChanged += (_, _) => throw new InvalidOperationException("pane bug");
        query.SqlChanged += (_, _) => calls++;

        document.Sql = "select 1";

        Assert.Equal(1, calls);
        Assert.Equal([(OutputLevel.Error, "DataTray.App.Tests: pane bug")], reported);
    }

    [Fact]
    public async Task ContextChanged_FiresOnce_WhenDatabaseListFails()
    {
        var failing = Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("no access"));
        var document = QueryDocumentFixture.NewDocument(out var connections, failing);
        IQueryDocument query = document;
        var seen = new List<string?>();
        query.ContextChanged += (_, _) => seen.Add(query.Database);

        document.InitQuery(connections.List().First(c => c.Id == "c1"), "Sales");
        await QueryDocumentFixture.WaitFor(() => seen.Count > 0);

        Assert.Equal(["Sales"], seen);
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

    [Fact]
    public void DisposePanes_WhileLinked_RaisesUnlinkedOnce()
    {
        var (document, slot, plugin) = OpenablePane();
        document.TogglePaneCommand.Execute(slot);
        var query = Assert.IsAssignableFrom<IQueryPaneContext>(plugin.LastContext).Document;
        query.Link("Builder");
        var unlinked = 0;
        query.Unlinked += (_, _) => unlinked++;

        document.DisposePanes();

        Assert.Equal((false, 1), (document.IsSqlLinked, unlinked));
    }

    [Fact] // Review Focus 1: a closed tab must not run plugin code when a pending refresh lands late.
    public void DisposePanes_LateRefresh_DoesNotCallAppliesTo()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        var document = QueryDocumentFixture.NewDocument(out var connections, QueryDocumentFixture.Ready("master"), pending.Task);
        document.InitQuery(connections.List().First(c => c.Id == "c1"));
        var plugin = new FakePanePlugin(() => new PaneControl());
        Mount(document, plugin);
        document.Connection = connections.List().First(c => c.Id == "c2");
        document.DisposePanes();
        var callsAtClose = plugin.AppliesCalls;

        pending.SetResult(["staging"]);

        Assert.Equal(callsAtClose, plugin.AppliesCalls);
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
        return (document, Mount(document, plugin), plugin);
    }

    private static QueryPaneSlot Mount(DocumentViewModel document, FakePanePlugin plugin)
    {
        var pane = new SubsystemQueryPane("builder", "Builder", new HashSet<string>(), new FakePluginLocalizer(), plugin);
        document.MountQueryPanes([pane], new FakeHostUi(), _ => null);
        return document.QueryPanes[0];
    }

    private sealed class FakePanePlugin(Func<Control> create) : IQueryPanePlugin
    {
        public int CreateCalls { get; private set; }
        public IQueryPaneContext? LastContext { get; private set; }
        public string PaneId => "builder";
        public string Title => "Builder";
        public bool Applies { get; set; } = true;
        public int AppliesCalls { get; private set; }

        public bool AppliesTo(IQueryDocument document)
        {
            AppliesCalls++;
            return Applies;
        }

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
