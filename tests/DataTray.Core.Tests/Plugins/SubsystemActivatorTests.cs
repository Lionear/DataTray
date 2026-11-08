using Avalonia.Controls;
using DataTray.Core.Plugins;
using DataTray.Sdk.Extensibility;
using DataTray.Sdk.Provisioning;

namespace DataTray.Core.Tests.Plugins;

public class SubsystemActivatorTests
{
    private sealed class PanePlugin : ISubsystemPlugin, IQueryPanePlugin, INodeMenuPlugin
    {
        public string PaneId => "pane";
        public string Title => "Pane";
        public IReadOnlyList<NodeMenuContribution> NodeMenuItems => [];
        public void Initialize(IPluginRuntimeContext context) { }
        public bool AppliesTo(IQueryDocument document) => true;
        public Control CreatePane(IQueryPaneContext context) => new Control();
    }

    private static SubsystemActivationResult Activate(params string[] capabilities) =>
        new SubsystemActivator(
            [new SubsystemActivation("p", capabilities, new PanePlugin(), null, "P")],
            _ => throw new NotSupportedException(),
            _ => throw new NotSupportedException()).ActivateAll();

    [Theory]
    [InlineData(PluginCapabilities.QueryPane, 1)]
    [InlineData(PluginCapabilities.Menu, 0)]
    public void ActivateAll_QueryPane_OnlyWithCapability(string capability, int expected)
    {
        var result = Activate(capability);

        Assert.Equal(expected, result.QueryPanes.Count);
        if (expected == 1)
        {
            Assert.Equal("p", result.QueryPanes[0].PluginId);
            Assert.Equal("P", result.QueryPanes[0].PluginName);
            Assert.Contains(PluginCapabilities.QueryPane, result.QueryPanes[0].Capabilities);
        }
    }

    [Theory]
    [InlineData(PluginCapabilities.Menu, 1)]
    [InlineData(PluginCapabilities.QueryPane, 0)]
    public void ActivateAll_NodeMenu_RequiresMenuCapability(string capability, int expected) =>
        Assert.Equal(expected, Activate(capability).NodeMenus.Count);
}
