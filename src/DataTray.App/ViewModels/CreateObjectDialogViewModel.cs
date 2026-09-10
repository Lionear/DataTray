using System.Collections.ObjectModel;
using DataTray.Core.Localization;
using DataTray.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DataTray.App.ViewModels;

/// <summary>
/// Backs the DDL Create dialog ("New Database…"/"New Schema…"/"New Table…"): a name (+ a column grid
/// for tables) drives a live, editable SQL preview via the provider's <c>BuildCreateStatement</c>.
/// Confirm hands the (possibly user-edited) preview text back to <see cref="MainViewModel"/>, which
/// runs it via <c>ExecuteDdlAsync</c> and refreshes the tree — this dialog never touches the database.
/// </summary>
public partial class CreateObjectDialogViewModel : ViewModelBase
{
    private IDbProvider _provider = null!;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _sqlPreview = string.Empty;

    public CreateObjectDialogViewModel(ILocalizer localizer)
    {
        Loc = localizer;
    }

    public ILocalizer Loc { get; }

    public DbObjectKind Kind { get; private set; }

    private string? ParentSchema { get; set; }

    public ObservableCollection<NewColumnInput> Columns { get; } = [];

    /// <summary>The key columns of a new index, in order. Separate from <see cref="Columns"/>: those are
    /// columns being defined, these are existing columns being referred to.</summary>
    public ObservableCollection<NewIndexColumnInput> IndexColumns { get; } = [];

    public IReadOnlyList<string> ColumnTypes { get; private set; } = [];

    /// <summary>The target table's existing columns, offered as the index picker's suggestions.</summary>
    public IReadOnlyList<string> TableColumns { get; private set; } = [];

    public bool IsTable => Kind == DbObjectKind.Table;

    public bool IsIndex => Kind == DbObjectKind.Index;

    [ObservableProperty]
    private bool _unique;

    private string? Table { get; set; }

    public string DialogTitle => Kind switch
    {
        DbObjectKind.Database => Loc["NewDatabase"],
        DbObjectKind.Schema => Loc["NewSchema"],
        DbObjectKind.Index => Loc["NewIndex"],
        _ => Loc["NewTable"]
    };

    /// <summary>A name, and — for a table — at least one named column, is required. The SQL preview
    /// itself is free-form once generated, so it isn't part of this gate.</summary>
    public bool CanSave => !string.IsNullOrWhiteSpace(Name)
        && (!IsTable || (Columns.Count > 0 && Columns.All(c => !string.IsNullOrWhiteSpace(c.Name))))
        && (!IsIndex || (IndexColumns.Count > 0 && IndexColumns.All(c => !string.IsNullOrWhiteSpace(c.Name))));

    /// <summary>Reset the dialog for a specific create action. Called once per open instead of via the
    /// constructor, mirroring <c>ConnectionDialogViewModel.LoadForEdit</c> — the VM comes from a
    /// DI factory that can't take per-invocation arguments.</summary>
    public void Configure(
        IDbProvider provider,
        DbObjectKind kind,
        string? parentSchema,
        string? table = null,
        IReadOnlyList<string>? tableColumns = null)
    {
        _provider = provider;
        Kind = kind;
        ParentSchema = parentSchema;
        ColumnTypes = provider.ColumnTypes;
        Table = table;
        TableColumns = tableColumns ?? [];

        Name = string.Empty;
        Unique = false;
        Columns.Clear();
        IndexColumns.Clear();
        if (kind == DbObjectKind.Table)
        {
            AddColumn();
        }
        else if (kind == DbObjectKind.Index)
        {
            AddIndexColumn();
        }

        OnPropertyChanged(nameof(IsTable));
        OnPropertyChanged(nameof(IsIndex));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(ColumnTypes));
        OnPropertyChanged(nameof(TableColumns));
        RefreshPreview();
    }

    partial void OnNameChanged(string value) => RefreshPreview();

    partial void OnUniqueChanged(bool value) => RefreshPreview();

    [RelayCommand]
    private void AddIndexColumn()
    {
        var column = new NewIndexColumnInput();
        column.PropertyChanged += (_, _) => RefreshPreview();
        IndexColumns.Add(column);
        RefreshPreview();
    }

    [RelayCommand]
    private void RemoveIndexColumn(NewIndexColumnInput? column)
    {
        if (column is null)
        {
            return;
        }

        IndexColumns.Remove(column);
        RefreshPreview();
    }

    [RelayCommand]
    private void AddColumn()
    {
        var column = new NewColumnInput(ColumnTypes.FirstOrDefault() ?? string.Empty);
        column.PropertyChanged += (_, _) => RefreshPreview();
        Columns.Add(column);
        RefreshPreview();
    }

    [RelayCommand]
    private void RemoveColumn(NewColumnInput? column)
    {
        if (column is null)
        {
            return;
        }

        Columns.Remove(column);
        RefreshPreview();
    }

    // Auto-regenerates the preview from the current name/columns. A manual edit of SqlPreview itself
    // sticks until the next name/column change overwrites it — Confirm always runs whatever text is
    // showing, edited or not, so a user tweak is never silently discarded except by their own further edits.
    private void RefreshPreview()
    {
        OnPropertyChanged(nameof(CanSave));

        if (string.IsNullOrWhiteSpace(Name))
        {
            SqlPreview = string.Empty;
            return;
        }

        var columns = IsTable
            ? Columns.Select(c => new NewColumnSpec(c.Name, c.Type, c.Nullable, c.PrimaryKey, c.AutoIncrement)).ToList()
            : (IReadOnlyList<NewColumnSpec>)[];
        var indexColumns = IsIndex
            ? IndexColumns.Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => new NewIndexColumnSpec(c.Name, c.Descending)).ToList()
            : (IReadOnlyList<NewIndexColumnSpec>)[];
        var spec = new CreateObjectSpec(Kind, Name, ParentSchema, columns, Table, indexColumns, Unique);

        try
        {
            SqlPreview = _provider.BuildCreateStatement(spec).Text;
        }
        catch (Exception ex)
        {
            SqlPreview = $"-- {ex.Message}";
        }
    }
}

/// <summary>One key column of a new index: an existing column's name and its sort direction.</summary>
public sealed partial class NewIndexColumnInput : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _descending;
}

/// <summary>One row in the DDL Create table dialog's column grid.</summary>
public sealed partial class NewColumnInput(string defaultType) : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _type = defaultType;

    [ObservableProperty]
    private bool _nullable = true;

    [ObservableProperty]
    private bool _primaryKey;

    [ObservableProperty]
    private bool _autoIncrement;
}
