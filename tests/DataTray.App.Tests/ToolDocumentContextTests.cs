using DataTray.App.ViewModels;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Localization;
using DataTray.Sdk.Schema;

namespace DataTray.App.Tests;

/// <summary>
/// SE-216: the host side of a plugin-owned tab's context. Every host action is a callback the opener
/// supplied, so what is worth pinning is that each one reaches the right callback and that the one guard
/// in here — a blank title must not wipe the tab's name — actually holds.
/// </summary>
public class ToolDocumentContextTests
{
    private static ToolDocumentContext Build(
        Action<string>? setTitle = null,
        Action<string>? openQuery = null,
        Action? close = null) =>
        new(
            provider: null!,
            profile: new ConnectionProfile { Name = "Prod", ConnectionString = "Host=db" },
            node: new DbNodeRef(DbNodeKind.Table, "orders"),
            localizer: EmptyPluginLocalizer.Instance,
            setTitle: setTitle ?? (_ => { }),
            openQueryEditor: openQuery ?? (_ => { }),
            closeDocument: close ?? (() => { }));

    [Fact]
    public void SetTitle_renames_the_tab()
    {
        string? renamed = null;
        Build(setTitle: t => renamed = t).SetTitle("ER · public");

        Assert.Equal("ER · public", renamed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_title_is_ignored_rather_than_clearing_the_tab(string? title)
    {
        // A tab with no name is unclickable in the strip and unrecoverable — the plugin cannot undo it.
        var renames = 0;
        Build(setTitle: _ => renames++).SetTitle(title!);

        Assert.Equal(0, renames);
    }

    [Fact]
    public void OpenQueryEditor_hands_the_sql_to_the_host()
    {
        string? received = null;
        Build(openQuery: sql => received = sql).OpenQueryEditor("SELECT 1");

        Assert.Equal("SELECT 1", received);
    }

    [Fact]
    public void CloseDocument_closes_the_tab()
    {
        var closed = false;
        Build(close: () => closed = true).CloseDocument();

        Assert.True(closed);
    }

    [Fact]
    public void The_launch_target_is_exposed_to_the_view()
    {
        var context = Build();

        Assert.Equal("orders", context.Node?.Name);
        Assert.Equal("Prod", context.Profile.Name);
        Assert.NotNull(context.Localizer);
    }
}
