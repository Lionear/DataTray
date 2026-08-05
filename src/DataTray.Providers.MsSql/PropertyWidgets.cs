using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;

namespace DataTray.Providers.MsSql;

// The two widgets every SSMS-style properties page in this provider is built from. They started nested
// inside DatabasePropertiesView; Agent job properties (SE-235) needs the same two, and a second copy of a
// grid-building class is exactly the kind of duplication that drifts.

/// <summary>Label/value property page (SSMS' left-label, right-value grid), grouped into sections.</summary>
internal sealed class PropPage
{
    public StackPanel Stack { get; } = new() { Spacing = 2 };
    public Dictionary<string, TextBlock> Values { get; } = new();

    public void Section(string header) => Stack.Children.Add(new TextBlock
    {
        Text = header,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, Stack.Children.Count == 0 ? 0 : 12, 0, 4)
    });

    public void Row(string label, string key)
    {
        var value = new TextBlock { Text = "…", TextWrapping = TextWrapping.Wrap, Opacity = 0.9 };
        Values[key] = value;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), Margin = new Thickness(0, 1, 0, 1) };
        var name = new TextBlock { Text = label, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(value, 1);
        row.Children.Add(name);
        row.Children.Add(value);
        Stack.Children.Add(row);
    }

    public void Set(string key, string? text)
    {
        if (Values.TryGetValue(key, out var tb))
        {
            Dispatcher.UIThread.Post(() => tb.Text = string.IsNullOrEmpty(text) ? "—" : text);
        }
    }

    public void Fail(Exception ex)
    {
        foreach (var (key, tb) in Values)
        {
            if (tb.Text is "…")
            {
                Set(key, "—");
            }
        }
        var first = Values.Keys.FirstOrDefault();
        if (first is not null)
        {
            Set(first, $"(unavailable: {ex.Message})");
        }
    }
}

/// <summary>Read-only tabular page built from a header row plus dynamically added value rows. Columns
/// have fixed pixel widths (wide enough that text wraps on word boundaries, not per-character) and the
/// whole grid scrolls horizontally when it is wider than the dialog.</summary>
internal sealed class Table
{
    private readonly Grid _grid;
    private readonly TextBlock _status;
    private readonly int _columns;

    public Control Control { get; }

    public Table(string[] headers, double[] widths)
    {
        _columns = headers.Length;
        _grid = new Grid();
        foreach (var w in widths)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(w, GridUnitType.Pixel)));
        }
        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < headers.Length; c++)
        {
            var header = new TextBlock
            {
                Text = headers[c],
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 0, 12, 6)
            };
            Grid.SetColumn(header, c);
            Grid.SetRow(header, 0);
            _grid.Children.Add(header);
        }

        _status = new TextBlock { Text = "…", Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0) };
        Control = new StackPanel
        {
            Children =
            {
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = _grid
                },
                _status
            }
        };
    }

    public void Fill(IReadOnlyList<string[]> rows) => Dispatcher.UIThread.Post(() =>
    {
        for (var i = _grid.Children.Count - 1; i >= 0; i--)
        {
            if (Grid.GetRow(_grid.Children[i]) > 0)
            {
                _grid.Children.RemoveAt(i);
            }
        }
        while (_grid.RowDefinitions.Count > 1)
        {
            _grid.RowDefinitions.RemoveAt(_grid.RowDefinitions.Count - 1);
        }

        _status.IsVisible = rows.Count == 0;
        _status.Text = "None";

        for (var r = 0; r < rows.Count; r++)
        {
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < _columns; c++)
            {
                var cell = new TextBlock
                {
                    Text = c < rows[r].Length ? rows[r][c] : "",
                    Opacity = 0.9,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 12, 1)
                };
                Grid.SetColumn(cell, c);
                Grid.SetRow(cell, r + 1);
                _grid.Children.Add(cell);
            }
        }
    });

    public void Fail(Exception ex) => Dispatcher.UIThread.Post(() =>
    {
        _status.IsVisible = true;
        _status.Text = $"(unavailable: {ex.Message})";
    });
}
