using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
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

    /// <summary>
    /// A row whose value column holds a control rather than a label. <paramref name="write"/> puts a loaded
    /// value into it and <paramref name="read"/> takes the current one back out, so the page's loader keeps
    /// calling <see cref="Set"/> either way and does not care which rows are editable.
    /// </summary>
    /// <remarks>
    /// <paramref name="read"/> returns the value in the vocabulary of whatever will be written — "ON" rather
    /// than "True" — so the before/after snapshot the caller diffs needs no second translation. That matters
    /// because a translation applied to one side and not the other is a change that looks real and is not.
    /// </remarks>
    public void Edit(string label, string key, Control editor, Action<string> write, Func<string> read)
    {
        Editors[key] = (write, read);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), Margin = new Thickness(0, 2, 0, 2) };
        var name = new TextBlock
        {
            Text = label,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(editor, 1);
        row.Children.Add(name);
        row.Children.Add(editor);
        Stack.Children.Add(row);
    }

    public Dictionary<string, (Action<string> Write, Func<string> Read)> Editors { get; } = new();

    /// <summary>The current value of every editable row, in the vocabulary its writer uses. Taken once when
    /// the page has loaded and again on OK; the difference is what gets written.</summary>
    public Dictionary<string, string> Snapshot() =>
        Editors.ToDictionary(e => e.Key, e => e.Value.Read(), StringComparer.Ordinal);

    public void Set(string key, string? text)
    {
        if (Editors.TryGetValue(key, out var editor))
        {
            Dispatcher.UIThread.Post(() => editor.Write(text ?? ""));
            return;
        }

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
            var cells = new List<Border>();
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

/// <summary>
/// The pieces the editable Agent job pages are laid out from. They started as a copy of the same two static
/// helpers in three pages, which is how three pages drift into looking like three products.
/// </summary>
internal static class FormBits
{
    /// <summary>How wide an editor column gets. Wide enough for a path or a command, narrow enough that a
    /// maximised dialog does not stretch a text box across the whole screen.</summary>
    public const double ColumnWidth = 620;

    /// <summary>Half a column, for two short fields side by side.</summary>
    private const double HalfWidth = (ColumnWidth - 16) / 2;

    public static TextBlock Section(string header) => new()
    {
        Text = header,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 6, 0, 0)
    };

    /// <summary>A label above its editor.</summary>
    public static Control Labelled(string label, Control editor) => new StackPanel
    {
        Spacing = 3,
        Children = { new TextBlock { Text = label, Opacity = 0.65 }, editor }
    };

    /// <summary>A label above a run of controls that belong together (a number and its unit, say).</summary>
    public static Control Row(string label, params Control[] controls)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var control in controls)
        {
            line.Children.Add(control);
        }

        return Labelled(label, line);
    }

    /// <summary>Two labelled fields side by side, each half a column.</summary>
    public static Control Pair(Control left, Control right)
    {
        left.Width = HalfWidth;
        right.Width = HalfWidth;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children = { left, right }
        };
    }

    /// <summary>The page body: one column, left-aligned, so nothing stretches to the window's width.</summary>
    public static Control Page(params Control[] children)
    {
        var stack = new StackPanel
        {
            Spacing = 10,
            MaxWidth = ColumnWidth,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return new ScrollViewer { Content = stack, Padding = new Thickness(4, 0, 14, 12) };
    }
}

/// <summary>One cell: its text, and optionally a colour when the value carries a verdict.</summary>
internal sealed record Cell(string Text, IBrush? Foreground = null);

/// <summary>
/// A table you can pick a row in — the list half of every master/detail page in the Agent job dialog. The
/// plugin cannot reach a DataGrid (it references Avalonia core only), and a ListBox of packed sentences is
/// what this replaces: "1 — Full backup (TSQL, last: succeeded)" is a row pretending to be four columns.
/// </summary>
internal sealed class SelectTable
{
    private readonly Grid _grid = new();
    // One entry per row, holding that row's cell borders — the highlight covers the whole width.
    private readonly List<List<Border>> _rows = [];
    private readonly TextBlock _empty = new() { Opacity = 0.7, Margin = new Thickness(9, 8, 0, 0) };
    private readonly int _columns;

    private int _selected = -1;

    public SelectTable(string[] headers, double[] widths, double height = 132)
    {
        _columns = headers.Length;
        foreach (var w in widths)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition(w <= 0
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(w, GridUnitType.Pixel)));
        }

        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < headers.Length; c++)
        {
            var header = new TextBlock
            {
                Text = headers[c],
                FontWeight = FontWeight.SemiBold,
                Opacity = 0.75,
                Margin = new Thickness(9, 4, 9, 5)
            };
            Grid.SetColumn(header, c);
            _grid.Children.Add(header);
        }

        Control = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            CornerRadius = new CornerRadius(4),
            Height = height,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new StackPanel { Children = { _grid, _empty } }
            }
        };
    }

    public Control Control { get; }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            _selected = value;
            for (var i = 0; i < _rows.Count; i++)
            {
                IBrush brush = i == value
                    ? new SolidColorBrush(Color.FromArgb(60, 90, 140, 240))
                    : Brushes.Transparent;
                foreach (var cell in _rows[i])
                {
                    cell.Background = brush;
                }
            }

            SelectionChanged?.Invoke();
        }
    }

    public event Action? SelectionChanged;

    /// <summary>Replace every row. <paramref name="emptyText"/> shows when there are none.</summary>
    public void Fill(IReadOnlyList<Cell[]> rows, string emptyText) => Dispatcher.UIThread.Post(() =>
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

        _rows.Clear();
        _empty.Text = emptyText;
        _empty.IsVisible = rows.Count == 0;

        for (var r = 0; r < rows.Count; r++)
        {
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var cells = new List<Border>();
            for (var c = 0; c < _columns; c++)
            {
                var cell = c < rows[r].Length ? rows[r][c] : new Cell("");
                // Every cell of a row is its own Border so the whole width highlights, not just the text.
                // Assigning a null Foreground is not "leave the default" in Avalonia, it is "no brush" —
                // and a cell with no brush draws nothing at all.
                var text = new TextBlock { Text = cell.Text, TextWrapping = TextWrapping.Wrap };
                if (cell.Foreground is not null)
                {
                    text.Foreground = cell.Foreground;
                }

                var box = new Border { Padding = new Thickness(9, 3, 9, 3), Child = text };
                var index = r;
                box.PointerPressed += (_, _) => SelectedIndex = index;
                Grid.SetColumn(box, c);
                Grid.SetRow(box, r + 1);
                _grid.Children.Add(box);
                cells.Add(box);
            }

            _rows.Add(cells);
        }

        // Re-apply the highlight to whatever row index is current.
        SelectedIndex = rows.Count == 0 ? -1 : Math.Clamp(_selected, 0, rows.Count - 1);
    });

    public void Fail(Exception ex) => Dispatcher.UIThread.Post(() =>
    {
        _empty.IsVisible = true;
        _empty.Text = ex.Message;
    });
}
