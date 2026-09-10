using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace DataTray.Viewers.Basic;

/// <summary>
/// Renders the selected row as a JSON object tree. A text cell that itself holds JSON is parsed into a
/// subtree rather than shown as an escaped string — which is the whole reason to reach for this view on a
/// table with a <c>jsonb</c> column.
/// </summary>
public sealed class JsonTreeViewer : IViewerPlugin
{
    public string Id => "json-tree";

    public string Title => "JSON";

    public string? TitleKey => "JsonViewerTitle";

    /// <summary>Any result set can be read as JSON — even one column of one row.</summary>
    public bool CanView(ResultView result) => result.Columns.Count > 0;

    public Control CreateView(IViewerContext context) => new JsonTreeView(context);
}

internal sealed class JsonTreeView : UserControl
{
    private readonly IViewerContext _context;
    private readonly TreeView _tree = new() { Margin = new Thickness(6) };
    private readonly TextBlock _empty;

    public JsonTreeView(IViewerContext context)
    {
        _context = context;
        _empty = new TextBlock
        {
            Text = context.Localizer.Get("NoRowSelected"),
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7
        };

        Content = new Panel { Children = { new ScrollViewer { Content = _tree }, _empty } };

        context.DataChanged += (_, _) => Render();
        context.SelectionChanged += (_, _) => Render();
        Render();
    }

    private void Render()
    {
        var result = _context.Result;
        var rowIndex = _context.SelectedRowIndex;

        if (rowIndex is not { } index || index < 0 || index >= result.Rows.Count)
        {
            _tree.ItemsSource = null;
            _empty.IsVisible = true;
            return;
        }

        _empty.IsVisible = false;
        var row = result.Rows[index];
        var nodes = new List<TreeNode>(result.Columns.Count);
        for (var i = 0; i < result.Columns.Count && i < row.Length; i++)
        {
            nodes.Add(BuildNode(result.Columns[i].Name, row[i]));
        }

        _tree.ItemsSource = nodes;
        _tree.ItemTemplate = TreeNode.Template;
    }

    // A cell becomes a subtree when its text parses as a JSON object or array; anything else is a leaf.
    // Parsing is attempted per cell rather than gated on the column type, because the engines disagree on
    // whether JSON arrives as a json/jsonb type or plain text.
    private static TreeNode BuildNode(string name, object? value)
    {
        if (value is null or DBNull)
        {
            return new TreeNode($"{name}: null", []);
        }

        if (value is byte[] bytes)
        {
            return new TreeNode($"{name}: <binary, {Format(bytes.Length)}>", []);
        }

        if (value is string text && LooksLikeJson(text) && TryParse(text) is { } parsed)
        {
            return new TreeNode(name, FromElement(parsed));
        }

        return new TreeNode($"{name}: {Render(value)}", []);
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.AsSpan().Trim();
        return trimmed.Length > 1
            && (trimmed[0] == '{' && trimmed[^1] == '}' || trimmed[0] == '[' && trimmed[^1] == ']');
    }

    private static JsonElement? TryParse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<TreeNode> FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .Select(p => Leaf(p.Name, p.Value))
            .ToList(),
        JsonValueKind.Array => element.EnumerateArray()
            .Select((v, i) => Leaf($"[{i}]", v))
            .ToList(),
        _ => []
    };

    private static TreeNode Leaf(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object or JsonValueKind.Array => new TreeNode(name, FromElement(value)),
        JsonValueKind.String => new TreeNode($"{name}: \"{value.GetString()}\"", []),
        JsonValueKind.Null => new TreeNode($"{name}: null", []),
        _ => new TreeNode($"{name}: {value.GetRawText()}", [])
    };

    private static string Render(object value) => value switch
    {
        bool b => b ? "true" : "false",
        DateTime d => d.ToString("O"),
        DateTimeOffset d => d.ToString("O"),
        string s => $"\"{s}\"",
        _ => value.ToString() ?? string.Empty
    };

    private static string Format(int byteCount) => byteCount switch
    {
        < 1024 => $"{byteCount} B",
        < 1024 * 1024 => $"{byteCount / 1024.0:0.#} kB",
        _ => $"{byteCount / (1024.0 * 1024):0.#} MB"
    };
}

/// <summary>One line in the tree. Flat text plus children — the viewer is read-only, so nothing more is
/// needed and the whole node is one <c>TextBlock</c>.</summary>
internal sealed record TreeNode(string Text, IReadOnlyList<TreeNode> Children)
{
    public static readonly IDataTemplate Template = new FuncTreeDataTemplate<TreeNode>(
        (node, _) => new TextBlock
        {
            Text = node.Text,
            FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
            FontSize = 11.5
        },
        node => node.Children);
}
