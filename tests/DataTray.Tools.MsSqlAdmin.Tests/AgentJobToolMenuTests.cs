using DataTray.Sdk;
using DataTray.Sdk.Tools;

namespace DataTray.Tools.MsSqlAdmin.Tests;

/// <summary>
/// Where the Agent job actions land on the menu (SE-261). Asserted through <see cref="IToolPlugin"/> rather
/// than the concrete class on purpose: <c>IsNodeAction</c> has a default implementation of <c>false</c>, so a
/// declaration that stopped satisfying the interface member would still compile, still read as <c>true</c> on
/// the class, and quietly put all six actions back under Tools ▸ — the bug this ticket fixes, returning
/// invisibly.
/// </summary>
public class AgentJobToolMenuTests
{
    public static TheoryData<IToolPlugin> AllAgentJobTools =>
    [
        new StartAgentJobTool(),
        new StopAgentJobTool(),
        new EnableAgentJobTool(),
        new DisableAgentJobTool(),
        new NewAgentJobTool(),
        new DeleteAgentJobTool(),
    ];

    [Theory]
    [MemberData(nameof(AllAgentJobTools))]
    public void Every_agent_job_action_renders_on_the_node_menu_not_under_Tools(IToolPlugin tool)
    {
        Assert.True(tool.IsNodeAction);
    }

    [Theory]
    [MemberData(nameof(AllAgentJobTools))]
    public void Only_delete_asks_for_confirmation(IToolPlugin tool)
    {
        // Starting, stopping, enabling and disabling are all undone by the opposite verb; a dropped job is gone.
        var destructive = tool.Id.Contains("delete");

        Assert.Equal(destructive, tool.IsDestructive);
    }
}
