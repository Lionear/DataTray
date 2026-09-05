using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace DataTray.Providers.MsSql;

// The widgets every SSMS-style properties page in this provider is built from. They started nested inside
// DatabasePropertiesView; Agent job properties (SE-235) needs the same ones, and a second copy of a
// grid-building class is exactly the kind of duplication that drifts.
//
// Colours come from the host theme by DynamicResource, never from a hardcoded grey or an Opacity guess.
// That is not only about dark/light switching: a page built from ad-hoc opacities sits next to the rest of
// the app looking almost right, which reads worse than looking different. plugins/Tools.CopyTable is the
// reference for how a code-built plugin view is supposed to do this.

/// <summary>The shared metrics, so every page in this provider has one rhythm rather than one per author.</summary>
internal static class PropMetrics
{
    /// <summary>Width of the label column. Fits "Auto Update Statistics Asynchronously" on two lines.</summary>
    public const double LabelWidth = 250;

    /// <summary>Minimum height of a property row. Set so a row holding text and a row holding a control are
    /// the same height — otherwise a page alternates between tight and loose as the control types change,
    /// which is most of what made the first version look unfinished.</summary>
    public const double RowHeight = 30;

    public static T Themed<T>(this T control, AvaloniaProperty property, string resource) where T : AvaloniaObject
    {
        control[!property] = new DynamicResourceExtension(resource);
        return control;
    }
}

/// <summary>Label/value property page (SSMS' left-label, right-value grid), grouped into sections.</summary>
internal sealed class PropPage
{
    private readonly TextBlock _noticeText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12
    };

    private readonly TextBlock _noticeDetail = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        Margin = new Thickness(0, 3, 0, 0)
    };

    private readonly Border _notice;

    public PropPage()
    {
        _noticeText.Themed(TextBlock.ForegroundProperty, "SETextPrimaryBrush");
        _noticeDetail.Themed(TextBlock.ForegroundProperty, "SETextFaintBrush");

        _notice = new Border
        {
            IsVisible = false,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 9),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new StackPanel { Children = { _noticeText, _noticeDetail } }
        };
        _notice.Themed(Border.BackgroundProperty, "SESecondaryBgBrush");
        _notice.Themed(Border.BorderBrushProperty, "SEHairlineBrush");

        Stack.Children.Add(_notice);
    }

    public StackPanel Stack { get; } = new();
    public Dictionary<string, TextBlock> Values { get; } = new();

    public void Section(string header)
    {
        var text = new TextBlock
        {
            Text = header,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            // The notice occupies index 0 whether or not it is showing, so "first section" is 1, not 0.
            Margin = new Thickness(0, Stack.Children.Count <= 1 ? 0 : 18, 0, 7)
        };
        text.Themed(TextBlock.ForegroundProperty, "SETextPrimaryBrush");
        Stack.Children.Add(text);
    }

    public void Row(string label, string key)
    {
        var value = new TextBlock
        {
            Text = "…",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        value.Themed(TextBlock.ForegroundProperty, "SETextPrimaryBrush");
        Values[key] = value;
        Stack.Children.Add(BuildRow(label, value));
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
        editor.HorizontalAlignment = HorizontalAlignment.Left;
        editor.VerticalAlignment = VerticalAlignment.Center;
        Stack.Children.Add(BuildRow(label, editor));
    }

    // One row shape for text and controls alike: same height, same label column, both halves centred against
    // each other. A label that top-aligns beside a centred control is the tell that a form was assembled
    // rather than laid out.
    private static Control BuildRow(string label, Control value)
    {
        var name = new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        name.Themed(TextBlock.ForegroundProperty, "SETextSecondaryBrush");

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{PropMetrics.LabelWidth},*"),
            MinHeight = PropMetrics.RowHeight
        };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(value, 1);
        row.Children.Add(name);
        row.Children.Add(value);
        return row;
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

    /// <summary>
    /// The page could not read its data. Every row degrades to an em dash and the reason goes in a notice at
    /// the top of the page.
    /// </summary>
    /// <remarks>
    /// This used to write <c>"(unavailable: {message})"</c> into the <em>first row of the page</em>, which is
    /// how a SQL Server column-name error ended up rendered as a database's collation — a raw server message
    /// sitting where a value belongs, blaming whichever row happened to be declared first. Now the page says
    /// what happened in its own voice and keeps the server's wording underneath in faint text, where it is
    /// still there to diagnose with and no longer pretending to be a setting.
    /// </remarks>
    public void Fail(Exception ex) => Fail("Some of these settings could not be read.", ex.Message);

    public void Fail(string message, string? detail = null) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var (key, tb) in Values)
        {
            if (tb.Text is "…")
            {
                Set(key, "—");
            }
        }

        _noticeText.Text = message;
        _noticeDetail.Text = detail ?? "";
        _noticeDetail.IsVisible = !string.IsNullOrWhiteSpace(detail);
        _notice.IsVisible = true;
    });
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

    /// <param name="height">Fixed height, for a table stacked with others on a scrolling page — inside a
    /// ScrollViewer there is no height to fill, so three tables in a row would each collapse to their
    /// content and the page would have no rhythm. Null on a page where the table is the subject and takes
    /// the space itself.</param>
    public Table(string[] headers, double[] widths, double? height = null)
    {
        _columns = headers.Length;
        _grid = new Grid();
        foreach (var w in widths)
        {
            // A width of 0 or less means "take what is left", as it already does in SelectTable. The column
            // that most needs to be read in full — a file's path, a permission's name — gets the leftover
            // instead of a guess that was too narrow the moment someone had a long one.
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
                FontSize = 11.5,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(10, 7, 12, 7)
            };
            header.Themed(TextBlock.ForegroundProperty, "SETextSecondaryBrush");
            Grid.SetColumn(header, c);
            Grid.SetRow(header, 0);
            _grid.Children.Add(header);
        }

        _status = new TextBlock { Text = "…", Margin = new Thickness(10, 8, 10, 8) };
        _status.Themed(TextBlock.ForegroundProperty, "SETextFaintBrush");

        // A bordered container rather than a bare grid: on a page that also holds label/value rows, an
        // unframed grid reads as more rows rather than as a table. Rows scroll inside the frame, so the
        // frame can be given the page's height without the content having to fill it.
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _grid
        };
        var inner = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(scroller, 0);
        Grid.SetRow(_status, 1);
        inner.Children.Add(scroller);
        inner.Children.Add(_status);

        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = inner
        };
        if (height is { } fixedHeight)
        {
            frame.Height = fixedHeight;
        }

        frame.Themed(Border.BorderBrushProperty, "SEHairlineBrush");
        Control = frame;
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
                var text = c < rows[r].Length ? rows[r][c] : "";
                // One line per row, trimmed rather than wrapped, with the whole value on hover. Wrapping
                // made a row with one long value twice the height of its neighbours, so a grid of mostly
                // short values had a ragged left edge down the other columns — and a value long enough to
                // wrap was usually long enough to be worth reading somewhere it fits anyway.
                var cell = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 6, 12, 6),
                    [ToolTip.TipProperty] = string.IsNullOrEmpty(text) ? null : text
                };
                cell.Themed(TextBlock.ForegroundProperty, "SETextPrimaryBrush");
                Grid.SetColumn(cell, c);
                Grid.SetRow(cell, r + 1);
                _grid.Children.Add(cell);
            }
        }
    });

    /// <summary>Same rule as <see cref="PropPage.Fail(Exception)"/>: the grid says what happened in its own
    /// words, and the server's wording stays as the tooltip rather than being shouted in the page.</summary>
    public void Fail(Exception ex) => Dispatcher.UIThread.Post(() =>
    {
        _status.IsVisible = true;
        _status.Text = "This could not be read.";
        ToolTip.SetTip(_status, ex.Message);
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

    public static TextBlock Section(string header)
    {
        var text = new TextBlock
        {
            Text = header,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 0)
        };
        return text.Themed(TextBlock.ForegroundProperty, "SETextPrimaryBrush");
    }

    /// <summary>A field label — secondary text, so the value beside it is what the eye lands on.</summary>
    public static TextBlock Label(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        return block.Themed(TextBlock.ForegroundProperty, "SETextSecondaryBrush");
    }

    /// <summary>Explanatory text under a control — quieter again than a label.</summary>
    public static TextBlock Hint(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
        return block.Themed(TextBlock.ForegroundProperty, "SETextFaintBrush");
    }

    /// <summary>A boolean setting. A ToggleSwitch, never a CheckBox, and with no On/Off caption — the row's
    /// own label says what it is, and the caption flipping between two words as you click is noise. Same
    /// call the host's tool dialog and settings window make.</summary>
    public static ToggleSwitch Toggle() => new() { OnContent = "", OffContent = "" };

    /// <summary>A boolean setting that carries its own caption, for use outside a label/value row. The
    /// caption is the same in both states: it names the setting, it does not report the state — the switch
    /// already does that, and a word that changes as you click is one more thing to read.</summary>
    public static ToggleSwitch Toggle(string label) => new() { OnContent = label, OffContent = label };

    /// <summary>A label above its editor.</summary>
    public static Control Labelled(string label, Control editor) => new StackPanel
    {
        Spacing = 3,
        Children = { Label(label), editor }
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
    private readonly TextBlock _empty = new() { Margin = new Thickness(9, 8, 0, 0) };
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
                FontSize = 11.5,
                Margin = new Thickness(9, 6, 9, 6)
            };
            header.Themed(TextBlock.ForegroundProperty, "SETextSecondaryBrush");
            Grid.SetColumn(header, c);
            _grid.Children.Add(header);
        }

        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Height = height,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new StackPanel { Children = { _grid, _empty } }
            }
        };
        frame.Themed(Border.BorderBrushProperty, "SEHairlineBrush");
        Control = frame;
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
                foreach (var cell in _rows[i])
                {
                    if (i == value)
                    {
                        cell.Themed(Border.BackgroundProperty, "SESelectionBgBrush");
                    }
                    else
                    {
                        // Clearing the binding as well as the value: leaving it bound would keep repainting
                        // the row selected the next time the theme changed.
                        cell.ClearValue(Border.BackgroundProperty);
                        cell.Background = Brushes.Transparent;
                    }
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
