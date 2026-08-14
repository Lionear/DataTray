namespace DataTray.Core.Toolbar;

/// <summary>
/// Persists the user's toolbar arrangement — order plus visibility — as a whole. Unlike
/// <see cref="Shortcuts.IKeymapStore"/> this is not a diff against defaults: order has no meaningful
/// "changed" subset, so the file holds the full list once the user has touched it. A fresh install has
/// no file at all, which <see cref="ToolbarLayoutService"/> reads as "everything, in catalog order".
/// </summary>
public interface IToolbarLayoutStore
{
    IReadOnlyList<ToolbarLayoutItem> Load();

    void Save(IReadOnlyList<ToolbarLayoutItem> layout);
}
