using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;

namespace DataTray.Viewers.Basic;

/// <summary>
/// Decodes the selected row's first binary column to a bitmap. Applicability is decided on column metadata
/// only — the host calls <see cref="CanView"/> on every refresh, so scanning the rows would cost a page turn.
/// That means the viewer stays on offer for a binary column whose bytes turn out not to be an image; it says
/// so in place rather than vanishing from the switcher mid-browse.
/// </summary>
public sealed class ImageViewer : IViewerPlugin
{
    public string Id => "image";

    public string Title => "Image";

    public string? TitleKey => "ImageViewerTitle";

    public bool CanView(ResultView result) => result.Columns.Any(IsBinary);

    public Control CreateView(IViewerContext context) => new ImageView(context);

    internal static bool IsBinary(ResultColumn column) => column.ClrType == typeof(byte[]);
}

internal sealed class ImageView : UserControl
{
    private readonly IViewerContext _context;
    private readonly Image _image = new() { Stretch = Avalonia.Media.Stretch.Uniform, Margin = new Thickness(12) };
    private readonly TextBlock _message;

    public ImageView(IViewerContext context)
    {
        _context = context;
        _message = new TextBlock
        {
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.7
        };

        Content = new Panel { Children = { _image, _message } };

        context.DataChanged += (_, _) => Render();
        context.SelectionChanged += (_, _) => Render();
        Render();
    }

    private void Render()
    {
        var result = _context.Result;

        // Prefer the selected column when it is itself binary, so a row with two blobs shows the one the
        // user actually clicked; otherwise fall back to the first binary column.
        var columnIndex = _context.SelectedColumnIndex is { } selected
            && selected >= 0 && selected < result.Columns.Count
            && ImageViewer.IsBinary(result.Columns[selected])
                ? selected
                : IndexOfFirstBinary(result);

        if (columnIndex < 0)
        {
            Show(null, _context.Localizer.Get("NoImageColumn"));
            return;
        }

        if (_context.SelectedRowIndex is not { } rowIndex
            || rowIndex < 0 || rowIndex >= result.Rows.Count
            || columnIndex >= result.Rows[rowIndex].Length)
        {
            Show(null, _context.Localizer.Get("NoRowSelected"));
            return;
        }

        if (result.Rows[rowIndex][columnIndex] is not byte[] { Length: > 0 } bytes)
        {
            Show(null, _context.Localizer.Get("EmptyCell"));
            return;
        }

        if (TryDecode(bytes) is { } bitmap)
        {
            Show(bitmap, null);
            return;
        }

        Show(null, $"{_context.Localizer.Get("NotAnImage")} · {string.Format(_context.Localizer.Get("Bytes"), bytes.Length)}");
    }

    private static int IndexOfFirstBinary(ResultView result)
    {
        for (var i = 0; i < result.Columns.Count; i++)
        {
            if (ImageViewer.IsBinary(result.Columns[i]))
            {
                return i;
            }
        }

        return -1;
    }

    // Avalonia's decoder throws on anything it doesn't recognise, and a BLOB column holds arbitrary bytes,
    // so "not an image" is the normal case here rather than an error worth surfacing as one.
    private static Bitmap? TryDecode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Show(Bitmap? bitmap, string? message)
    {
        _image.Source = bitmap;
        _image.IsVisible = bitmap is not null;
        _message.Text = message;
        _message.IsVisible = message is not null;
    }
}
