using Avalonia.Media;

namespace DataTray.Sdk.Extensibility;

/// <summary>
/// One action a plugin adds to the application toolbar at the top of the main window: a stable
/// <see cref="Id"/> (namespaced by the host as <c>pluginId:localId</c> — it is what the user's toolbar
/// layout and the keymap both reference, so it must stay stable across versions), a localised
/// <see cref="Title"/>, and the action to run when it is clicked, handed an <see cref="IHostUi"/> so it can
/// open a dialog. The plugin already holds its <see cref="IPluginRuntimeContext"/> from Initialize for
/// everything else.
/// </summary>
/// <remarks>
/// The user decides whether the button is actually shown and where it sits (Settings ▸ Toolbar). A
/// contribution is a proposal, not a place: a plugin cannot claim the front, reorder or remove host
/// actions, or exempt itself from the overflow flyout.
/// </remarks>
public sealed record ToolbarContribution(
    string Id,
    string Title,
    Func<IHostUi, Task> InvokeAsync)
{
    /// <summary>Stroked vector geometry for the button's icon, drawn <c>Stretch="Uniform"</c> and tinted
    /// with the theme. The plugin owns it — host icon resources are unreachable across the ALC boundary.
    /// <c>null</c> renders a text-only button.</summary>
    public Geometry? Icon { get; init; }

    /// <summary>Tooltip; falls back to <see cref="Title"/> when null. The host appends the plugin's name,
    /// so the user can always tell where a button came from.</summary>
    public string? Tooltip { get; init; }

    /// <summary>
    /// A <em>suggested</em> key in Avalonia gesture syntax, where <c>Mod</c> stands for the platform's
    /// primary modifier (Cmd on macOS, Ctrl elsewhere). Only a suggestion: every toolbar action is
    /// rebindable in Settings ▸ Keyboard whether or not the plugin proposes one, and <c>null</c> — the
    /// default — ships the action unbound but still bindable.
    /// </summary>
    public string? DefaultGesture { get; init; }
}

/// <summary>
/// Optional contribution a standing-subsystem plugin (<see cref="ISubsystemPlugin"/>) may implement to add
/// buttons to the application toolbar. Gated by the <see cref="PluginCapabilities.Toolbar"/> capability —
/// a separate string from <see cref="PluginCapabilities.Menu"/> on purpose: a menu item sits behind a click
/// in a place the user goes looking for it, a toolbar button is permanent chrome in the app's most valuable
/// strip, and those are different things to ask for.
/// </summary>
public interface IToolbarPlugin
{
    /// <summary>The application-toolbar actions this plugin contributes.</summary>
    IReadOnlyList<ToolbarContribution> ToolbarItems { get; }
}
