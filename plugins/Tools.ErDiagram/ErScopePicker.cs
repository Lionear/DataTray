using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace DataTray.Tools.ErDiagram;

/// <summary>
/// The picker the diagram opens on (SE-217): every table in the schema, nothing ticked, and a Draw button
/// that is disabled until something is. Defaulting to everything selected would be friendlier for the
/// three-table demo and useless on a real database, which is the case that decides it.
///
/// <para>The "pick some tables, then Draw" hint lives in the tab's status line rather than in here, both
/// because the mockup puts it there and because two copies of the same sentence on one screen reads as a
/// mistake.</para>
/// </summary>
public sealed class ErScopePicker : UserControl
{
    private readonly IReadOnlyList<TableDef> _tables;
    private readonly Func<string, string> _localize;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _checkboxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
    private readonly Button _draw;

    public ErScopePicker(IReadOnlyList<TableDef> tables, Func<string, string> localize)
    {
        _tables = tables;
        _localize = localize;

        var list = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(4) };
        foreach (var table in tables.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
        {
            var box = new CheckBox { Content = table.Key, FontSize = 12, Tag = table.Key };
            box.IsCheckedChanged += (_, _) => Toggle(table.Key, box.IsChecked == true);
            _checkboxes[table.Key] = box;
            list.Children.Add(box);
        }

        _draw = new Button { Content = localize("er.pick.draw"), IsEnabled = false };
        _draw.Click += (_, _) => Drawn?.Invoke(_selected.ToList());

        var cancel = new Button { Content = localize("er.pick.cancel") };
        cancel.Click += (_, _) => Cancelled?.Invoke();

        var all = new Button { Content = localize("er.pick.all") };
        all.Click += (_, _) => Select(tables.Select(t => t.Key));

        var none = new Button { Content = localize("er.pick.none") };
        none.Click += (_, _) => Select([]);

        // "+ Related" is the feature that makes this usable on a real database — and the first thing to cut
        // if the diagram ever has to be small. It grows the selection rather than replacing it.
        var related = new Button { Content = localize("er.pick.related") };
        related.Click += (_, _) => Select(ErScope.ExpandOneHop(_tables, _selected));

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Avalonia.Thickness(8),
            Children = { all, none, related },
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _count, cancel, _draw },
        };

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(actions);
        root.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        Content = root;
        UpdateCount();
    }

    /// <summary>Raised with the chosen table keys when Draw is pressed.</summary>
    public event Action<IReadOnlyList<string>>? Drawn;

    /// <summary>Raised when the user backs out — the tab closes rather than showing an empty canvas.</summary>
    public event Action? Cancelled;

    private void Toggle(string key, bool on)
    {
        if (on)
        {
            _selected.Add(key);
        }
        else
        {
            _selected.Remove(key);
        }

        _draw.IsEnabled = _selected.Count > 0;
        UpdateCount();
    }

    private void Select(IEnumerable<string> keys)
    {
        var wanted = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);

        // Drive the checkboxes and let their handler maintain the set, so the ticks and the selection can
        // never disagree.
        foreach (var (key, box) in _checkboxes)
        {
            box.IsChecked = wanted.Contains(key);
        }
    }

    private void UpdateCount() =>
        _count.Text = string.Format(_localize("er.pick.count"), _selected.Count, _tables.Count);
}
