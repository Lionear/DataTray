using Avalonia.Controls;
using Avalonia.Input;
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

    [Fact] // The pane column shows one pane at a time.
    public void TogglePane_OpeningSecond_ClosesFirst()
    {
        var document = QueryDocument();
        var first = new FakePanePlugin(() => new PaneControl());
        var second = new FakePanePlugin(() => new PaneControl());
        document.MountQueryPanes([Pane(first), Pane(second)], new FakeHostUi(), _ => null);
        var (firstSlot, secondSlot) = (document.QueryPanes[0], document.QueryPanes[1]);

        document.TogglePaneCommand.Execute(firstSlot);
        var firstControl = Assert.IsType<PaneControl>(firstSlot.Control);
        document.TogglePaneCommand.Execute(secondSlot);

        Assert.Equal((false, true), (firstSlot.IsOpen, secondSlot.IsOpen));
        Assert.True(firstControl.Disposed);
    }

    [Fact] // The view hosts OpenPane, so it must announce every open and close.
    public void OpenPane_FollowsToggle_AndRaisesPropertyChanged()
    {
        var (document, slot, _) = OpenablePane();
        var names = new List<string?>();
        document.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        document.TogglePaneCommand.Execute(slot);
        var whileOpen = document.OpenPane;
        document.TogglePaneCommand.Execute(slot);

        Assert.Same(slot, whileOpen);
        Assert.Null(document.OpenPane);
        Assert.Equal(2, names.Count(name => name == nameof(DocumentViewModel.OpenPane)));
    }

    [Fact]
    public void MountQueryPanes_Shortcut_ParsesToGesture()
    {
        var document = QueryDocument();
        var plugin = new FakePanePlugin(() => new PaneControl()) { Shortcut = "Ctrl+Shift+B" };

        document.MountQueryPanes([Pane(plugin)], new FakeHostUi(), _ => null);

        Assert.Equal(new KeyGesture(Key.B, KeyModifiers.Control | KeyModifiers.Shift), document.QueryPanes[0].Gesture);
    }

    [Fact] // A plugin's malformed shortcut is its problem: reported to Output, no binding, the pane still works.
    public void MountQueryPanes_MalformedShortcut_ReportsAndBindsNothing()
    {
        var document = QueryDocument();
        var reported = new List<(OutputLevel, string)>();
        document.Reported += (level, message) => reported.Add((level, message));
        var plugin = new FakePanePlugin(() => new PaneControl()) { Shortcut = "Ctrl+Nope" };

        document.MountQueryPanes([Pane(plugin)], new FakeHostUi(), _ => null);

        Assert.Null(document.QueryPanes[0].Gesture);
        Assert.Equal([(OutputLevel.Error, "Builder: invalid shortcut 'Ctrl+Nope'")], reported);
    }

    [Theory] // The toggle group and its hairline only show when a pane applies to the tab.
    [InlineData(true)]
    [InlineData(false)]
    public void HasAvailablePanes_FollowsAppliesTo(bool applies)
    {
        var document = QueryDocument();
        var plugin = new FakePanePlugin(() => new PaneControl()) { Applies = applies };

        document.MountQueryPanes([Pane(plugin)], new FakeHostUi(), _ => null);

        Assert.Equal(applies, document.HasAvailablePanes);
    }

    [Fact]
    public void MountQueryPanes_RaisesHasAvailablePanes()
    {
        var document = QueryDocument();
        var names = new List<string?>();
        document.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        Mount(document, new FakePanePlugin(() => new PaneControl()));

        Assert.Contains(nameof(DocumentViewModel.HasAvailablePanes), names);
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

    [Theory] // Consent boundary: only a pane granted `schema` gets to read the connection's tables.
    [InlineData(PluginCapabilities.Schema, true)]
    [InlineData(PluginCapabilities.QueryPane, false)]
    public void SchemaFor_GivesASchemaOnlyWithTheSchemaCapability(string capability, bool expected)
    {
        var pane = new SubsystemQueryPane("builder", "Builder", new HashSet<string> { capability },
            new FakePluginLocalizer(), new FakePanePlugin(() => new PaneControl()));

        var schema = MainViewModel.SchemaFor(pane, new QueryDocumentFixture.FakeSchemaCache(), QueryDocument());

        Assert.Equal(expected, schema is not null);
    }

    [Fact] // The button and the shortcut both go through FormatCommand; the fake dialect throws if it formats.
    public void Format_WhileLinked_IsDisabledAndLeavesSqlAlone()
    {
        var document = QueryDocument();
        IQueryDocument query = document;
        query.Link("Builder");
        query.SetSql("select 1");

        var canFormat = document.FormatCommand.CanExecute(null);
        document.FormatCommand.Execute(null);

        Assert.Equal((false, "select 1"), (canFormat, document.Sql));
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

    [Fact] // Duplicate tab and Reopen closed tab both go through CaptureTab/ApplyTab.
    public void ApplyTab_CarriesSqlAndPaneStateToTheNewTab()
    {
        var (first, firstSlot, firstPlugin) = OpenablePane();
        first.TogglePaneCommand.Execute(firstSlot);
        Assert.IsAssignableFrom<IQueryPaneContext>(firstPlugin.LastContext).State = "{\"tables\":[\"Orders\"]}";
        ((IQueryDocument)first).SetSql("select * from Orders");
        var (second, secondSlot, secondPlugin) = OpenablePane();

        second.ApplyTab(first.CaptureTab());
        second.TogglePaneCommand.Execute(secondSlot);

        var state = Assert.IsAssignableFrom<IQueryPaneContext>(secondPlugin.LastContext).State;
        Assert.Equal(("select * from Orders", "{\"tables\":[\"Orders\"]}"), (second.Sql, state));
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
        document.MountQueryPanes([Pane(plugin)], new FakeHostUi(), _ => null);
        return document.QueryPanes[0];
    }

    private static SubsystemQueryPane Pane(FakePanePlugin plugin) =>
        new("builder", "Builder", new HashSet<string>(), new FakePluginLocalizer(), plugin);

    private sealed class FakePanePlugin(Func<Control> create) : IQueryPanePlugin
    {
        public int CreateCalls { get; private set; }
        public IQueryPaneContext? LastContext { get; private set; }
        public string PaneId => "builder";
        public string Title => "Builder";
        public string? Shortcut { get; init; }
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
