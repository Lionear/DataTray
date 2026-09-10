using DataTray.Core.Toolbar;

namespace DataTray.Core.Tests.Toolbar;

/// <summary>
/// The resolve rules of the toolbar layout (SE-255 §3.2). These are the parts that are easy to get wrong
/// and invisible when they are: a new plugin's button has to appear on its own, and a plugin that blinks
/// out must not cost the user their arrangement.
/// </summary>
public class ToolbarLayoutServiceTests
{
    private static ToolbarActionEntry Plugin(string id, string title = "Plugin") =>
        new(id, title, ToolbarActionSource.Plugin, title);

    [Fact]
    public void Resolve_WithNoSavedLayout_ReturnsCatalogOrderAllVisible()
    {
        var service = new ToolbarLayoutService(new FakeStore(), [Plugin("p:one")]);

        var layout = service.Resolve();

        Assert.Equal(
            [ToolbarCatalog.Ids.NewQueryTab, ToolbarCatalog.Ids.GoToObject, "p:one"],
            layout.Select(i => i.Id));
        Assert.All(layout, i => Assert.True(i.Visible));
    }

    [Fact]
    public void Resolve_KeepsSavedOrderAndVisibility()
    {
        var store = new FakeStore(
            new ToolbarLayoutItem(ToolbarCatalog.Ids.GoToObject, false),
            new ToolbarLayoutItem(ToolbarCatalog.Ids.NewQueryTab, true));

        var layout = new ToolbarLayoutService(store).Resolve();

        Assert.Equal([ToolbarCatalog.Ids.GoToObject, ToolbarCatalog.Ids.NewQueryTab], layout.Select(i => i.Id));
        Assert.False(layout[0].Visible);
    }

    [Fact]
    public void Resolve_AppendsAnUnmentionedActionVisible()
    {
        // A freshly installed plugin is absent from the file. Absent means new, not hidden.
        var store = new FakeStore(
            new ToolbarLayoutItem(ToolbarCatalog.Ids.NewQueryTab, false),
            new ToolbarLayoutItem(ToolbarCatalog.Ids.GoToObject, true));

        var layout = new ToolbarLayoutService(store, [Plugin("p:new")]).Resolve();

        Assert.Equal("p:new", layout[^1].Id);
        Assert.True(layout[^1].Visible);
    }

    [Fact]
    public void Resolve_SkipsASavedIdTheCatalogCannotResolve()
    {
        var store = new FakeStore(
            new ToolbarLayoutItem("p:gone", true),
            new ToolbarLayoutItem(ToolbarCatalog.Ids.NewQueryTab, true));

        var layout = new ToolbarLayoutService(store).Resolve();

        Assert.DoesNotContain(layout, i => i.Id == "p:gone");
    }

    [Fact]
    public void Apply_KeepsAnUnresolvedIdInItsPlace()
    {
        // "p:offline" belongs to a plugin that is disabled or mid-update: it is not in the catalog, so the
        // settings pane never shows it and cannot hand it back. It has to survive the save at its position.
        var store = new FakeStore(
            new ToolbarLayoutItem(ToolbarCatalog.Ids.NewQueryTab, true),
            new ToolbarLayoutItem("p:offline", false),
            new ToolbarLayoutItem(ToolbarCatalog.Ids.GoToObject, true));
        var service = new ToolbarLayoutService(store);

        service.Apply(
        [
            new ToolbarLayoutItem(ToolbarCatalog.Ids.NewQueryTab, true),
            new ToolbarLayoutItem(ToolbarCatalog.Ids.GoToObject, false),
        ]);

        Assert.Equal(
            [ToolbarCatalog.Ids.NewQueryTab, "p:offline", ToolbarCatalog.Ids.GoToObject],
            store.Saved.Select(i => i.Id));
        Assert.False(store.Saved.Single(i => i.Id == "p:offline").Visible);
    }

    [Fact]
    public void Apply_KeepsALeadingUnresolvedIdAtTheFront()
    {
        var store = new FakeStore(
            new ToolbarLayoutItem("p:offline", true),
            new ToolbarLayoutItem(ToolbarCatalog.Ids.NewQueryTab, true));
        var service = new ToolbarLayoutService(store);

        service.Apply([new ToolbarLayoutItem(ToolbarCatalog.Ids.NewQueryTab, true)]);

        Assert.Equal("p:offline", store.Saved[0].Id);
    }

    [Fact]
    public void Apply_ThenResolve_ReflectsTheNewOrder()
    {
        var service = new ToolbarLayoutService(new FakeStore());

        service.Apply(
        [
            new ToolbarLayoutItem(ToolbarCatalog.Ids.GoToObject, true),
            new ToolbarLayoutItem(ToolbarCatalog.Ids.NewQueryTab, false),
        ]);

        Assert.Equal([ToolbarCatalog.Ids.GoToObject], service.VisibleActions().Select(e => e.Id));
    }

    [Fact]
    public void Reset_FallsBackToCatalogOrderAllVisible()
    {
        var service = new ToolbarLayoutService(new FakeStore(
            new ToolbarLayoutItem(ToolbarCatalog.Ids.NewQueryTab, false)));

        service.Reset();

        Assert.Equal(
            [ToolbarCatalog.Ids.NewQueryTab, ToolbarCatalog.Ids.GoToObject],
            service.VisibleActions().Select(e => e.Id));
    }

    private sealed class FakeStore(params ToolbarLayoutItem[] initial) : IToolbarLayoutStore
    {
        public IReadOnlyList<ToolbarLayoutItem> Saved { get; private set; } = initial;

        public IReadOnlyList<ToolbarLayoutItem> Load() => Saved;

        public void Save(IReadOnlyList<ToolbarLayoutItem> layout) => Saved = layout;
    }
}
