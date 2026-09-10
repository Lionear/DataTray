namespace DataTray.Core.Toolbar;

/// <summary>Where a toolbar catalog entry came from — decides how its title is resolved and whether
/// the settings list names an owning plugin beside it.</summary>
public enum ToolbarActionSource
{
    Host,
    Plugin,
}
