using DataTray.Sdk.Localization;
using DataTray.Sdk.Viewers;

namespace DataTray.Core.Viewers;

/// <summary>All loaded viewer plugins, plus the "which apply to this result set" filter the View switcher
/// uses. Mirrors <c>IToolRegistry</c>.</summary>
public interface IViewerRegistry
{
    IReadOnlyList<IViewerPlugin> All { get; }

    /// <summary>Viewers that say they can render <paramref name="result"/>, in registration order. A viewer
    /// that throws from <c>CanView</c> is treated as inapplicable rather than taking the tab down with it —
    /// this runs on every refresh, on the UI thread.</summary>
    IReadOnlyList<IViewerPlugin> Applicable(ResultView result);

    /// <summary>The localizer for the plugin that ships <paramref name="viewerId"/>, or
    /// <see cref="EmptyPluginLocalizer.Instance"/> when it ships no translations — never null.</summary>
    IPluginLocalizer LocalizerFor(string viewerId);
}

/// <inheritdoc />
public sealed class ViewerRegistry : IViewerRegistry
{
    private readonly List<IViewerPlugin> _all;
    private readonly IReadOnlyDictionary<string, IPluginLocalizer> _localizers;

    public ViewerRegistry(IEnumerable<IViewerPlugin> viewers, IReadOnlyDictionary<string, IPluginLocalizer>? localizers = null)
    {
        _all = viewers.ToList();
        _localizers = localizers ?? new Dictionary<string, IPluginLocalizer>();
    }

    public IReadOnlyList<IViewerPlugin> All => _all;

    public IPluginLocalizer LocalizerFor(string viewerId) =>
        _localizers.TryGetValue(viewerId, out var localizer) ? localizer : EmptyPluginLocalizer.Instance;

    public IReadOnlyList<IViewerPlugin> Applicable(ResultView result) =>
        _all.Where(v => CanView(v, result)).ToList();

    private static bool CanView(IViewerPlugin viewer, ResultView result)
    {
        try
        {
            return viewer.CanView(result);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[viewer] {viewer.Id}: CanView threw, treating as inapplicable — {ex.Message}");
            return false;
        }
    }
}
