using System.Collections.Generic;

namespace DataTray.App.ViewModels;

/// <summary>A labelled section of shortcut rows (Tabs, Query, Editor, Search) in Settings › Keyboard.</summary>
public sealed class ShortcutGroup(string label, IReadOnlyList<ShortcutItem> items)
{
    public string Label { get; } = label;

    public IReadOnlyList<ShortcutItem> Items { get; } = items;
}
