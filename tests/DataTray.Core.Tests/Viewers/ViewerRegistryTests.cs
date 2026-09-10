using Avalonia.Controls;
using DataTray.Core.Viewers;
using DataTray.Sdk.Query;
using DataTray.Sdk.Viewers;

namespace DataTray.Core.Tests.Viewers;

public class ViewerRegistryTests
{
    private static ResultView View(params ResultColumn[] columns) =>
        new(columns, [], "postgres");

    private static ResultColumn Column(string name, Type type) => new(name, type);

    [Fact]
    public void Applicable_keeps_only_the_viewers_that_say_yes()
    {
        var registry = new ViewerRegistry([
            new StubViewer("always", _ => true),
            new StubViewer("never", _ => false)
        ]);

        var applicable = registry.Applicable(View(Column("id", typeof(int))));

        Assert.Equal(["always"], applicable.Select(v => v.Id));
    }

    // CanView runs on the UI thread on every refresh, for every installed viewer. A third-party plugin that
    // throws there must cost itself its slot in the switcher, not take the query tab down with it.
    [Fact]
    public void Applicable_treats_a_throwing_viewer_as_inapplicable()
    {
        var registry = new ViewerRegistry([
            new StubViewer("boom", _ => throw new InvalidOperationException("bad plugin")),
            new StubViewer("fine", _ => true)
        ]);

        var applicable = registry.Applicable(View(Column("id", typeof(int))));

        Assert.Equal(["fine"], applicable.Select(v => v.Id));
    }

    [Fact]
    public void LocalizerFor_falls_back_to_the_empty_localizer()
    {
        var registry = new ViewerRegistry([new StubViewer("json", _ => true)]);

        Assert.NotNull(registry.LocalizerFor("json"));
        Assert.NotNull(registry.LocalizerFor("no-such-viewer"));
    }

    private sealed class StubViewer(string id, Func<ResultView, bool> canView) : IViewerPlugin
    {
        public string Id { get; } = id;

        public string Title => Id;

        public bool CanView(ResultView result) => canView(result);

        public Control CreateView(IViewerContext context) => new ContentControl();
    }
}
