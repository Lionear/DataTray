using Avalonia.Media;
using DataTray.Sdk;
using DataTray.Sdk.Ui;

namespace DataTray.App.ViewModels;

/// <summary>
/// The semantic icon layer: it maps app concepts — a schema-tree node kind, a tab, a toolbar
/// action, a Settings category — onto the raw <see cref="Icons"/> geometries generated from Lucide
/// (see tools/generate-icons.py). Keeping the mapping here means views bind to a stable name like
/// <c>NodeIcons.Table</c> without caring which Lucide glyph backs it, and swapping a concept's icon
/// is a one-line change. The geometries are stroked line-icons, so render them with a themed Stroke
/// brush and <c>Stretch="Uniform"</c> — Lucide sits in a 24x24 box, unlike the old hand-drawn 16x16
/// set, but Uniform stretch makes the source box irrelevant.
/// </summary>
public static class NodeIcons
{
    // --- Schema-tree node kinds. ---
    public static readonly Geometry Connection = Icons.Server;
    public static readonly Geometry Database = Icons.Database;
    public static readonly Geometry Schema = Icons.Box;
    public static readonly Geometry Folder = Icons.Folder;
    public static readonly Geometry Star = Icons.Star;
    public static readonly Geometry Table = Icons.Table;
    public static readonly Geometry View = Icons.Eye;
    public static readonly Geometry Column = Icons.RectangleVertical;
    public static readonly Geometry Index = Icons.List;
    public static readonly Geometry Sequence = Icons.TrendingUp;
    public static readonly Geometry Object = Icons.Diamond;
    public static readonly Geometry Procedure = Icons.SquareTerminal;
    public static readonly Geometry Function = Icons.SquareFunction;
    public static readonly Geometry Trigger = Icons.Zap;
    public static readonly Geometry User = Icons.User;
    public static readonly Geometry AgentJob = Icons.Clock;

    // --- Toolbar action glyphs (Connection Manager). ---
    public static readonly Geometry Plus = Icons.Plus;
    public static readonly Geometry FolderPlus = Icons.FolderPlus;
    public static readonly Geometry Duplicate = Icons.Copy;
    public static readonly Geometry Trash = Icons.Trash2;
    public static readonly Geometry ImportConnections = Icons.Download;

    // --- Document tab-strip glyphs. ---
    public static readonly Geometry TabQuery = Icons.FileCode;
    public static readonly Geometry TabBrowse = Icons.Table;
    public static readonly Geometry TabMonitor = Icons.Clock;

    /// <summary>Fallback for a plugin-owned tab that supplies no icon of its own (SE-216). The puzzle
    /// piece is already the product's mark for "this came from a plugin".</summary>
    public static readonly Geometry TabPlugin = Icons.Puzzle;

    // --- Tool-window glyphs (status-bar / stripe toggles). ---
    public static readonly Geometry ToolOutput = Icons.Terminal;
    public static readonly Geometry ToolHistory = Icons.Clock;
    public static readonly Geometry AiActivity = Icons.Bot;

    // --- Warning banners. ---
    public static readonly Geometry Warning = Icons.TriangleAlert;

    // --- Settings category-rail glyphs. ---
    public static readonly Geometry SettingsGeneral = Icons.SlidersHorizontal;
    public static readonly Geometry SettingsAppearance = Icons.Palette;
    public static readonly Geometry SettingsEditor = Icons.Pencil;
    public static readonly Geometry SettingsQuery = Icons.Play;
    public static readonly Geometry SettingsKeyboard = Icons.Keyboard;
    public static readonly Geometry SettingsPlugins = Icons.Puzzle;

    public static Geometry For(DbNodeKind kind) => kind switch
    {
        DbNodeKind.Database => Database,
        DbNodeKind.Schema => Schema,
        DbNodeKind.SchemaFolder => Folder,
        DbNodeKind.TableFolder => Folder,
        DbNodeKind.ViewFolder => Folder,
        DbNodeKind.IndexFolder => Folder,
        DbNodeKind.SequenceFolder => Folder,
        DbNodeKind.ProcedureFolder => Folder,
        DbNodeKind.FunctionFolder => Folder,
        DbNodeKind.TriggerFolder => Folder,
        DbNodeKind.Group => Folder,
        DbNodeKind.DatabaseFolder => Folder,
        DbNodeKind.ColumnFolder => Folder,
        DbNodeKind.UserFolder => Folder,
        DbNodeKind.User => User,
        DbNodeKind.LoginFolder => Folder,
        DbNodeKind.Login => User,
        DbNodeKind.Table => Table,
        DbNodeKind.View => View,
        DbNodeKind.Column => Column,
        DbNodeKind.Index => Index,
        DbNodeKind.Sequence => Sequence,
        DbNodeKind.Procedure => Procedure,
        DbNodeKind.Function => Function,
        DbNodeKind.Trigger => Trigger,
        DbNodeKind.Object => Object,
        DbNodeKind.AgentJob => AgentJob,
        DbNodeKind.AgentJobFolder => Folder,
        _ => Connection
    };
}
