using DataTray.Sdk;
using DataTray.Sdk.Tools;

namespace DataTray.Tools.MsSqlAdmin.Tests;

/// <summary>
/// Where the index actions land on the menu (SE-253). Asserted through <see cref="IToolPlugin"/> rather than
/// the concrete class on purpose: <c>IsNodeAction</c> has a default implementation of <c>false</c>, so a
/// declaration that stopped satisfying the interface member would still compile, still read as <c>true</c> on
/// the class, and quietly put all seven actions back under Tools ▸ — the bug this ticket fixes, returning
/// invisibly.
/// </summary>
public class IndexToolMenuTests
{
    public static TheoryData<IToolPlugin> AllIndexTools =>
    [
        new RebuildAllIndexesTool(),
        new ReorganizeAllIndexesTool(),
        new DisableAllIndexesTool(),
        new RebuildIndexTool(),
        new ReorganizeIndexTool(),
        new DisableIndexTool(),
        new DropIndexTool(),
    ];

    [Theory]
    [MemberData(nameof(AllIndexTools))]
    public void Every_index_action_renders_on_the_node_menu_not_under_Tools(IToolPlugin tool)
    {
        Assert.True(tool.IsNodeAction);
    }

    [Theory]
    [MemberData(nameof(AllIndexTools))]
    public void Only_disable_and_drop_ask_for_confirmation(IToolPlugin tool)
    {
        // Rebuild and reorganise only cost time; disabling takes a table offline and dropping is for good.
        var destructive = tool.Id.Contains("disable") || tool.Id.Contains("drop");

        Assert.Equal(destructive, tool.IsDestructive);
    }
}
