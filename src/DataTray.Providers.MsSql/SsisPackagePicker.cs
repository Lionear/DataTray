using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DataTray.Providers.MsSql;

/// <summary>
/// Picks a package out of SSISDB: folder → project → package, three levels because that is all the catalog
/// has. Typing the path is still allowed on the page — this exists because every segment is case-sensitive to
/// the catalog's collation, and because selecting the project is what makes the environment list meaningful.
/// </summary>
internal sealed class SsisPackagePicker : Window
{
    private readonly Button _select = new() { Content = "Select", MinWidth = 96, IsEnabled = false };
    private readonly TreeView _tree = new();

    private SsisPackagePicker(IReadOnlyList<SsisPackageRef> packages, string server)
    {
        Title = $"Select a package — {server}";
        Width = 460;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _tree.ItemsSource = BuildTree(packages);
        _tree.SelectionChanged += (_, _) => _select.IsEnabled = Selected is not null;
        _tree.DoubleTapped += (_, _) =>
        {
            if (Selected is not null)
            {
                Close(Selected);
            }
        };

        var cancel = new Button { Content = "Cancel", MinWidth = 96 };
        cancel.Click += (_, _) => Close(null);
        _select.Click += (_, _) => Close(Selected);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { cancel, _select }
        };

        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(12) };
        Grid.SetRow(_tree, 0);
        Grid.SetRow(footer, 1);
        layout.Children.Add(_tree);
        layout.Children.Add(footer);
        Content = layout;
    }

    /// <summary>The selected package, or null while a folder or project is selected instead.</summary>
    private SsisPackageRef? Selected => (_tree.SelectedItem as TreeViewItem)?.Tag as SsisPackageRef;

    /// <summary>Opens the picker over <paramref name="owner"/> and returns the chosen package, or null.</summary>
    public static async Task<SsisPackageRef?> ShowAsync(
        Window owner, IReadOnlyList<SsisPackageRef> packages, string server)
    {
        var picker = new SsisPackagePicker(packages, server);
        return await picker.ShowDialog<SsisPackageRef?>(owner);
    }

    private static List<TreeViewItem> BuildTree(IReadOnlyList<SsisPackageRef> packages) =>
    [
        .. packages
            .GroupBy(p => p.Folder)
            .Select(folder => new TreeViewItem
            {
                Header = folder.Key,
                IsExpanded = true,
                ItemsSource = folder
                    .GroupBy(p => p.Project)
                    .Select(project => new TreeViewItem
                    {
                        Header = project.Key,
                        IsExpanded = true,
                        ItemsSource = project
                            // The .dtsx leaves are the only nodes carrying a Tag, which is what makes
                            // Select light up for a package and stay dark on a folder or a project.
                            .Select(package => new TreeViewItem { Header = package.Package, Tag = package })
                            .ToList()
                    })
                    .ToList()
            })
    ];
}
