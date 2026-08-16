namespace DataTray.Core.Toolbar;

/// <summary>
/// The host's own toolbar actions, in factory order — which is also the default layout (all visible,
/// this order). Mirrors <see cref="Shortcuts.ShortcutCatalog"/>: the <see cref="Ids"/> constants are the
/// single source of truth for the persisted keys, so <c>toolbar.json</c>, the keymap and the settings
/// pane all address the same strings.
/// </summary>
public static class ToolbarCatalog
{
    public static class Ids
    {
        public const string NewQueryTab = "NewQueryTab";
        public const string GoToObject = "GoToObject";
    }

    /// <summary>Host actions in factory order. <c>Title</c> is a resx key (see
    /// <see cref="ToolbarActionEntry"/>).</summary>
    public static IReadOnlyList<ToolbarActionEntry> Host { get; } =
    [
        new(Ids.NewQueryTab, "NewQueryTab", ToolbarActionSource.Host),
        new(Ids.GoToObject, "GoToObject", ToolbarActionSource.Host),
    ];
}
