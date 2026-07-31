using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using DataTray.Sdk.Localization;
using DataTray.Sdk.Tools;
using DataTray.Sdk.Ui;

namespace DataTray.Tools.ErDiagram;

/// <summary>
/// Draws a database's tables and the foreign keys between them (SE-82, phase 1: read-only).
///
/// <para>It opens as a <b>tab</b> rather than a dialog (<see cref="IToolDocumentUi"/>, SE-216): a diagram
/// is read alongside the queries it explains, and a dialog you have to dismiss to type a query is the
/// wrong container for it. Because of that, <see cref="ExecuteAsync"/> is never called — opening the tab
/// is the whole action — and <see cref="Fields"/> stays empty.</para>
///
/// <para>Everything it needs already exists: <c>SchemaReader</c> in <c>plugins/Shared.Schema</c> supplies
/// the tables and their <c>ForeignKeyDef</c>s, and <see cref="LayeredErLayout"/> turns those into
/// positions. This type is the seam between them and the host.</para>
/// </summary>
public sealed class ErDiagramTool : IToolPlugin, IToolDocumentUi
{
    // The engines Shared.Schema can read. Anywhere else the menu entry must be absent rather than open
    // an empty canvas.
    private static readonly string[] SupportedProviders = ["postgres", "mysql", "sqlserver", "sqlite"];

    public string Id => "er-diagram";
    public string Title => "ER Diagram";
    public string? TitleKey => "er.title";
    public string DialogTitle => "ER Diagram";
    public string? DialogTitleKey => "er.title";

    public string? Description =>
        "Draw this database's tables and the foreign keys between them. Nothing is changed.";

    public ToolTarget Target { get; } = new(
        ProviderIds: SupportedProviders,
        NodeKinds: [DbNodeKind.Database, DbNodeKind.Schema],
        IncludeConnectionRoot: true,
        ConnectionRootProviderIds: SupportedProviders);

    /// <summary>Empty: a document tool collects nothing. The tab is the interface.</summary>
    public IReadOnlyList<ToolField> Fields { get; } = [];

    /// <summary>Never called — <see cref="IToolDocumentUi"/> tools act by opening their tab.</summary>
    public Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> values,
        IProgress<ToolProgress> progress,
        CancellationToken ct) => Task.CompletedTask;

    public Geometry? Icon => null;

    public Control CreateDocument(IToolDocumentContext context)
    {
        var view = new ErDiagramView(context);
        // The schema read is a database round-trip, so the tab opens immediately on a "reading…" line and
        // fills in when it arrives. Blocking here would freeze the window while the tab is created.
        _ = view.LoadAsync();
        return view;
    }
}

/// <summary>
/// The tab's content: a status line plus the diagram, in a scroll viewer. Deliberately thin — it reads the
/// schema, hands it to the layout, and puts the resulting canvas on screen.
/// </summary>
/// <remarks>
/// Implements <see cref="IDisposable"/> so the host releases the schema snapshot when the tab closes; a
/// diagram of a large database is not something to keep alive for the life of the app.
/// </remarks>
public sealed class ErDiagramView : UserControl, IDisposable
{
    private readonly IToolDocumentContext _context;
    private readonly IPluginLocalizer _loc;
    private readonly TextBlock _status = new() { Margin = new Avalonia.Thickness(12, 8), FontSize = 12 };
    private readonly ScrollViewer _scroller = new();
    private readonly CancellationTokenSource _cancellation = new();

    private readonly StackPanel _toolbar = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Margin = new Avalonia.Thickness(8),
        IsVisible = false,
    };

    private ErGraph? _graph;
    private ErDiagramCanvas? _canvas;
    private IReadOnlyList<TableDef> _tables = [];

    public ErDiagramView(IToolDocumentContext context)
    {
        _context = context;
        _loc = context.Localizer;

        _status.Text = _loc.Get("er.loading");

        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(_toolbar, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(_toolbar);
        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.HorizontalAlignment = HorizontalAlignment.Stretch;
        _scroller.VerticalAlignment = VerticalAlignment.Stretch;
        root.Children.Add(_scroller);

        Content = root;
    }

    /// <summary>Detail level. Fixed at Keys for now; the picker that switches it is SE-216's next step.</summary>
    public ErDetail Detail { get; init; } = ErDetail.Keys;

    public async Task LoadAsync()
    {
        try
        {
            var reader = new SchemaReader(_context.Provider);
            var snapshot = await reader.ReadAsync(_context.Profile, _context.ProviderId, _cancellation.Token);

            _tables = snapshot.Tables;

            if (_tables.Count == 0)
            {
                _status.Text = _loc.Get("er.empty", _context.Profile.Name);
                return;
            }

            // Open on the picker, not on a canvas. A schema with two hundred tables drawn blind is a
            // hairball nobody reads, and the user is the only one who knows which corner they came for.
            ShowPicker();
        }
        catch (OperationCanceledException)
        {
            // The tab was closed while the schema was still being read. Nothing to report to nobody.
        }
        catch (Exception ex)
        {
            _status.Text = _loc.Get("er.error", ex.Message);
        }
    }

    private void ShowPicker()
    {
        var picker = new ErScopePicker(_tables, key => _loc.Get(key));
        picker.Drawn += Draw;
        picker.Cancelled += _context.CloseDocument;

        _scroller.Background = null;
        _scroller.Content = picker;
        _status.Text = _loc.Get("er.pick.hint");
    }

    private void BuildToolbar()
    {
        if (_toolbar.Children.Count > 0)
        {
            return;
        }

        var save = new Button { Content = _loc.Get("er.save") };
        save.Click += async (_, _) => await SaveAsync();

        var open = new Button { Content = _loc.Get("er.open") };
        open.Click += async (_, _) => await OpenAsync();

        var export = new Button { Content = _loc.Get("er.export") };
        export.Click += async (_, _) => await ExportAsync();

        _toolbar.Children.Add(save);
        _toolbar.Children.Add(open);
        _toolbar.Children.Add(export);
    }

    private async Task SaveAsync()
    {
        if (_graph is null)
        {
            return;
        }

        var suggested = $"{_context.Profile.Name}.{ErDiagramFile.Extension}";
        var path = await _context.PickSaveFileAsync(suggested, ErDiagramFile.Extension);
        if (path is null)
        {
            return;
        }

        var file = new ErDiagramFile
        {
            ProviderId = _context.ProviderId,
            ConnectionName = _context.Profile.Name,
            Database = _context.Profile.Database,
            Tables = _graph.Nodes.Select(n => n.Key).ToList(),
        };

        try
        {
            await File.WriteAllTextAsync(path, file.ToJson());
            _status.Text = _loc.Get("er.saved", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            _status.Text = _loc.Get("er.saveFailed", ex.Message);
        }
    }

    private async Task OpenAsync()
    {
        var path = await _context.PickOpenFileAsync(ErDiagramFile.Extension);
        if (path is null)
        {
            return;
        }

        try
        {
            var file = ErDiagramFile.FromJson(await File.ReadAllTextAsync(path));
            var resolved = file.ResolveAgainst(_tables);

            if (resolved.Present.Count == 0)
            {
                _status.Text = _loc.Get("er.openNothingLeft", Path.GetFileName(path));
                return;
            }

            Draw(resolved.Present);

            // Tables that have gone are reported, never quietly dropped: a table disappearing between
            // saving a diagram and opening it is exactly what the diagram is opened to find out.
            if (resolved.Missing.Count > 0)
            {
                _status.Text = _loc.Get("er.openMissing",
                    resolved.Present.Count, resolved.Missing.Count, string.Join(", ", resolved.Missing));
            }

            if (!string.Equals(file.ProviderId, _context.ProviderId, StringComparison.OrdinalIgnoreCase)
                && file.ProviderId.Length > 0)
            {
                _status.Text = _loc.Get("er.openOtherProvider", file.ProviderId, _context.ProviderId);
            }
        }
        catch (Exception ex)
        {
            _status.Text = _loc.Get("er.openFailed", ex.Message);
        }
    }

    private async Task ExportAsync()
    {
        if (_canvas is null)
        {
            return;
        }

        var suggested = $"{_context.Profile.Name}.{ErDiagramExport.Png}";
        var path = await _context.PickSaveFileAsync(suggested, ErDiagramExport.Png, ErDiagramExport.Svg);
        if (path is null)
        {
            return;
        }

        try
        {
            // The extension decides the format — the picker offers both and the user has already chosen
            // by the time we get here.
            if (Path.GetExtension(path).TrimStart('.').Equals(ErDiagramExport.Svg, StringComparison.OrdinalIgnoreCase))
            {
                ErDiagramExport.WriteSvg(_canvas, path);
            }
            else
            {
                ErDiagramExport.WritePng(_canvas, path);
            }

            _status.Text = _loc.Get("er.exported", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            _status.Text = _loc.Get("er.exportFailed", ex.Message);
        }
    }

    private void Draw(IReadOnlyList<string> selected)
    {
        var chosen = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        _graph = ErGraph.Build(_tables.Where(t => chosen.Contains(t.Key)));
        var layout = new LayeredErLayout().Compute(_graph);

        BuildToolbar();
        _toolbar.IsVisible = true;

        var palette = PaletteForHost();
        // Top-left, not centred: a diagram is read from its left edge, and a ScrollViewer would
        // otherwise float a small one in the middle of the tab.
        _scroller.Background = palette.Canvas;
        _canvas = new ErDiagramCanvas(_graph, layout, Detail, palette)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _scroller.Content = _canvas;

        // Relations to tables the user chose not to draw are counted rather than dropped — with a picker
        // in front of the canvas that is now the ordinary case, not an edge one.
        _status.Text = _graph.RelationsOutOfScope > 0
            ? _loc.Get("er.statusOutOfScope",
                _graph.Nodes.Count, _graph.Edges.Count, _graph.RelationsOutOfScope)
            : _loc.Get("er.status", _graph.Nodes.Count, _graph.Edges.Count);

        _context.SetTitle(_loc.Get("er.tab", _context.Profile.Name));
    }

    /// <summary>
    /// Light or dark, decided from the control's own inherited foreground rather than the host's theme
    /// resources — a plugin cannot reach those across the ALC boundary, but it does inherit the rendered
    /// colours, and a light foreground means a dark chrome.
    /// </summary>
    private ErPalette PaletteForHost()
    {
        if (Foreground is ISolidColorBrush brush)
        {
            var c = brush.Color;
            var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return luminance > 0.5 ? ErPalette.Dark : ErPalette.Light;
        }

        return ErPalette.Light;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        _scroller.Content = null;
        _canvas = null;
        _graph = null;
        _tables = [];
    }
}
