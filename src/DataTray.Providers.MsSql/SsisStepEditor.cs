using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DataTray.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace DataTray.Providers.MsSql;

/// <summary>
/// The command editor for an SSIS step (SE-236). Everywhere else on the Steps page the command is text and a
/// text box is the honest editor for it; here it is a generated dtexec argument string, and hand-editing that
/// is how you write a step that fails at run time with a message pointing nowhere.
/// </summary>
/// <remarks>
/// The command is derived from the fields and shown read-only. Reading one back matters more than the editor
/// does — nearly every SSIS step that reaches DataTray was written by SSMS — so a command carrying an option
/// this editor does not model does not open here at all: <see cref="Load"/> reports that and the page keeps
/// its text box. Dropping an option on save would change what the step does without anyone touching a field.
/// </remarks>
internal sealed class SsisStepEditor
{
    private static readonly (SsisPackageSource Source, string Label)[] Sources =
    [
        (SsisPackageSource.Catalog, "SSIS Catalog (SSISDB)"),
        (SsisPackageSource.FileSystem, "File system"),
        (SsisPackageSource.MsdbStore, "Package store — msdb"),
        (SsisPackageSource.ManagedFolderStore, "Package store — managed folder")
    ];

    // catalog.operations' logging levels, in the order the catalog numbers them.
    private static readonly (int Value, string Label)[] LoggingLevels =
    [
        (0, "None"), (1, "Basic"), (2, "Performance"), (3, "Verbose"), (4, "Runtime lineage")
    ];

    private const string NoEnvironment = "(none)";

    private readonly NodeInfoContext _context;

    private readonly ComboBox _source = new();
    private readonly TextBox _server = new();
    private readonly TextBox _path = new();
    private readonly Button _browse = new() { Content = "Browse…" };
    private readonly ComboBox _environment = new();
    private readonly ComboBox _logging = new();
    private readonly CheckBox _use32Bit = new() { Content = "Use 32-bit runtime" };
    private readonly CheckBox _wait = new() { Content = "Wait for the package to finish", IsChecked = true };
    private readonly TextBox _password = new() { PasswordChar = '•' };

    private readonly StackPanel _overrides = new() { Spacing = 4 };
    private readonly List<(CheckBox Use, string Name, TextBox Value)> _overrideRows = [];

    private readonly SelectableTextBlock _preview = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontFamily = FontFamily.Parse("Consolas, Menlo, monospace"),
        FontSize = 11,
        Opacity = 0.85
    };

    private readonly TextBlock _warning = new()
    {
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
        Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xA3, 0x3E))
    };

    private readonly Control _catalogOnly;
    private readonly Control _legacyOnly;
    private readonly Control _serverRow;

    private List<SsisPackageRef> _packages = [];

    /// <summary>
    /// The reference id behind each entry of <see cref="_environment"/>, same order, null for "(none)". Kept
    /// alongside the list rather than derived from the selected index, so an id the catalog could not resolve
    /// — or has not been asked about yet — still survives a save. Losing it here would quietly change what
    /// the step runs against, which is the failure this editor exists to prevent.
    /// </summary>
    private List<int?> _environmentIds = [null];

    private bool _loading;

    public SsisStepEditor(NodeInfoContext context)
    {
        _context = context;

        _source.ItemsSource = Sources.Select(s => s.Label).ToList();
        _source.SelectedIndex = 0;
        _logging.ItemsSource = LoggingLevels.Select(l => l.Label).ToList();
        _logging.SelectedIndex = 1;
        _environment.ItemsSource = new[] { NoEnvironment };
        _environment.SelectedIndex = 0;

        _serverRow = FormBits.Labelled("Server", _server);
        _catalogOnly = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                FormBits.Pair(
                    FormBits.Labelled("Environment", _environment),
                    FormBits.Labelled("Logging level", _logging)),
                _wait
            }
        };
        _legacyOnly = FormBits.Labelled("Package password", _password);

        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_path, 0);
        Grid.SetColumn(_browse, 1);
        _browse.Margin = new Thickness(8, 0, 0, 0);
        pathRow.Children.Add(_path);
        pathRow.Children.Add(_browse);

        Control = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _warning,
                FormBits.Section("Package"),
                FormBits.Pair(FormBits.Labelled("Source", _source), _serverRow),
                FormBits.Labelled("Package", pathRow),
                _legacyOnly,
                FormBits.Section("Execution options"),
                _catalogOnly,
                _use32Bit,
                FormBits.Section("Connection overrides"),
                _overrides,
                FormBits.Section("Command"),
                new Border
                {
                    Padding = new Thickness(9),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
                    Child = _preview
                }
            }
        };

        _source.SelectionChanged += (_, _) => { SyncVisibility(); Refresh(); };
        _path.TextChanged += (_, _) => OnPathChanged();
        _server.TextChanged += (_, _) => Refresh();
        _environment.SelectionChanged += (_, _) => Refresh();
        _logging.SelectionChanged += (_, _) => Refresh();
        _password.TextChanged += (_, _) => Refresh();
        _use32Bit.IsCheckedChanged += (_, _) => Refresh();
        _wait.IsCheckedChanged += (_, _) => Refresh();
        _browse.Click += async (_, _) => await BrowseAsync();

        SyncVisibility();
        Refresh();
        _ = LoadCatalogAsync();
    }

    public Control Control { get; }

    /// <summary>The command as the fields currently describe it — what the page saves.</summary>
    public string Command => SsisStepCommand.Build(Current());

    /// <summary>
    /// Fills the fields from an existing step. False when the command carries something this editor does not
    /// model, in which case nothing has been changed and the caller keeps its text box.
    /// </summary>
    public bool Load(string command)
    {
        // A new step arrives with no command at all; that is not a parse failure, it is a blank form.
        if (string.IsNullOrWhiteSpace(command))
        {
            Apply(new SsisStepCommand());
            return true;
        }

        var result = SsisStepCommand.Parse(command);
        if (!result.CanEdit)
        {
            return false;
        }

        Apply(result.Command!);
        return true;
    }

    /// <summary>Why a command could not be loaded, phrased for the banner the page shows instead.</summary>
    public static string? RefusalReason(string command)
    {
        var result = SsisStepCommand.Parse(command);
        if (result.CanEdit)
        {
            return null;
        }

        return result.UnsupportedOptions.Count > 0
            ? "Edited as text — this command uses options the editor does not model: "
              + string.Join(", ", result.UnsupportedOptions)
              + ". Nothing has been changed."
            : "Edited as text — this command does not name a package.";
    }

    private void Apply(SsisStepCommand step)
    {
        _loading = true;
        _source.SelectedIndex = Array.FindIndex(Sources, s => s.Source == step.Source);
        _path.Text = step.PackagePath;
        _server.Text = step.Server;
        _password.Text = step.PackagePassword ?? string.Empty;
        _use32Bit.IsChecked = step.Use32BitRuntime;
        _wait.IsChecked = step.WaitForCompletion;
        _logging.SelectedIndex = step.LoggingLevel is { } level
            ? Math.Max(0, Array.FindIndex(LoggingLevels, l => l.Value == level))
            : 1;
        BuildOverrideRows(step.ConnectionOverrides);
        // Hold the reference the command carried before the catalog has been asked about it. Until the read
        // lands this is the only place that id exists, and a save in between must not drop it.
        ShowEnvironments([], step.EnvironmentReference);
        _loading = false;

        SyncVisibility();
        _ = LoadPackageDetailAsync(step.EnvironmentReference);
        Refresh();
    }

    private SsisStepCommand Current() => new()
    {
        Source = Sources[Math.Clamp(_source.SelectedIndex, 0, Sources.Length - 1)].Source,
        PackagePath = _path.Text ?? string.Empty,
        Server = _server.Text ?? string.Empty,
        EnvironmentReference = SelectedEnvironment(),
        LoggingLevel = LoggingLevels[Math.Clamp(_logging.SelectedIndex, 0, LoggingLevels.Length - 1)].Value,
        Use32BitRuntime = _use32Bit.IsChecked == true,
        WaitForCompletion = _wait.IsChecked == true,
        PackagePassword = string.IsNullOrEmpty(_password.Text) ? null : _password.Text,
        ConnectionOverrides = _overrideRows
            .Where(row => row.Use.IsChecked == true)
            .Select(row => new SsisConnectionOverride(row.Name, row.Value.Text ?? string.Empty))
            .ToList()
    };

    private int? SelectedEnvironment()
    {
        var index = _environment.SelectedIndex;
        return index >= 0 && index < _environmentIds.Count ? _environmentIds[index] : null;
    }

    /// <summary>
    /// Points the dropdown at a list of environments, with <paramref name="reference"/> selected. A reference
    /// the catalog does not have keeps its own entry at the top rather than falling back to "(none)" — the id
    /// stays in the command until someone picks a replacement on purpose.
    /// </summary>
    private void ShowEnvironments(List<SsisEnvironmentRef> environments, int? reference)
    {
        var known = environments.FindIndex(e => e.ReferenceId == reference);
        var missing = reference is not null && known < 0;

        var labels = new List<string> { missing ? $"reference {reference} — missing" : NoEnvironment };
        var ids = new List<int?> { missing ? reference : null };
        foreach (var environment in environments)
        {
            labels.Add(environment.Label);
            ids.Add(environment.ReferenceId);
        }

        _environmentIds = ids;
        _environment.ItemsSource = labels;
        _environment.SelectedIndex = missing ? 0 : known + 1;

        Warn(missing
            ? $"Environment reference {reference} no longer exists. The step will fail when it runs if the "
              + "package has parameters. Pick a reference, or clear it if the package needs none."
            : null);
    }

    // Environments and logging exist only in the catalog; a password only outside it. Hiding rather than
    // disabling, so nobody reads a greyed-out environment box as "this package has none".
    private void SyncVisibility()
    {
        var source = Sources[Math.Clamp(_source.SelectedIndex, 0, Sources.Length - 1)].Source;
        _catalogOnly.IsVisible = source == SsisPackageSource.Catalog;
        _legacyOnly.IsVisible = source != SsisPackageSource.Catalog;
        _browse.IsVisible = source == SsisPackageSource.Catalog;
        // A file-system package lives on the Agent machine, which has no server name of its own.
        _serverRow.IsVisible = source != SsisPackageSource.FileSystem;
        _overrides.IsVisible = _overrideRows.Count > 0;
    }

    private void Refresh()
    {
        if (!_loading)
        {
            _preview.Text = Command;
        }
    }

    private void OnPathChanged()
    {
        if (!_loading)
        {
            _ = LoadPackageDetailAsync(null);
            Refresh();
        }
    }

    // ── Catalog ──────────────────────────────────────────────────────────────────────────────────────

    private async Task LoadCatalogAsync()
    {
        try
        {
            await using var connection = new SqlConnection(_context.Profile.ConnectionString);
            await connection.OpenAsync();
            var packages = await SsisCatalog.PackagesAsync(connection);
            Dispatcher.UIThread.Post(() => _packages = packages);
        }
        catch (Exception ex)
        {
            // A catalog that cannot be read is not fatal: the path can still be typed. Say so rather than
            // failing the page, because SSIS steps exist that never touch SSISDB.
            Warn($"The SSIS catalog could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Refills the environment list for whichever project the current path names, and reports a reference the
    /// catalog no longer has — the most common broken SSIS step there is, and one whose run-time error names
    /// nothing useful.
    /// </summary>
    private async Task LoadPackageDetailAsync(int? wanted)
    {
        var package = Parse(_path.Text);
        if (package is null)
        {
            return;
        }

        try
        {
            await using var connection = new SqlConnection(_context.Profile.ConnectionString);
            await connection.OpenAsync();
            var environments = await SsisCatalog.EnvironmentsAsync(connection, package.Folder, package.Project);
            var managers = await SsisCatalog.ConnectionManagersAsync(connection, package.Folder, package.Project);

            Dispatcher.UIThread.Post(() =>
            {
                _loading = true;
                ShowEnvironments(environments, wanted);
                MergeOverrideRows(managers);
                _loading = false;
                Refresh();
            });
        }
        catch (Exception ex)
        {
            Warn($"The package's environments could not be read: {ex.Message}");
        }
    }

    /// <summary>Splits <c>\SSISDB\folder\project\package.dtsx</c>, or null when the path is not one.</summary>
    private static SsisPackageRef? Parse(string? path)
    {
        var parts = (path ?? string.Empty).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 4 && parts[0].Equals("SSISDB", StringComparison.OrdinalIgnoreCase)
            ? new SsisPackageRef(parts[1], parts[2], parts[3])
            : null;
    }

    private async Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(Control) is not Window owner)
        {
            return;
        }

        var chosen = await SsisPackagePicker.ShowAsync(owner, _packages, _server.Text ?? string.Empty);
        if (chosen is not null)
        {
            _path.Text = chosen.Path;
        }
    }

    // ── Connection overrides ─────────────────────────────────────────────────────────────────────────

    /// <summary>Rows for the overrides the command already carries, before the catalog is known.</summary>
    private void BuildOverrideRows(IReadOnlyList<SsisConnectionOverride> existing)
    {
        _overrideRows.Clear();
        _overrides.Children.Clear();
        foreach (var item in existing)
        {
            AddOverrideRow(item.Name, item.Value, used: true);
        }

        _overrides.IsVisible = _overrideRows.Count > 0;
    }

    /// <summary>
    /// Adds the connection managers the catalog reports, keeping the values the command already set. An
    /// unticked row shows what the package or environment supplies and writes nothing to the command.
    /// </summary>
    private void MergeOverrideRows(IReadOnlyList<string> managers)
    {
        foreach (var manager in managers.Where(m => _overrideRows.All(r => r.Name != m)))
        {
            AddOverrideRow(manager, string.Empty, used: false);
        }

        _overrides.IsVisible = _overrideRows.Count > 0;
    }

    private void AddOverrideRow(string name, string value, bool used)
    {
        var use = new CheckBox { IsChecked = used, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock
        {
            Text = name,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var editor = new TextBox { Text = value, IsEnabled = used };

        use.IsCheckedChanged += (_, _) =>
        {
            editor.IsEnabled = use.IsChecked == true;
            Refresh();
        };
        editor.TextChanged += (_, _) => Refresh();

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(0, 1) };
        Grid.SetColumn(use, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(editor, 2);
        label.Margin = new Thickness(6, 0, 6, 0);
        row.Children.Add(use);
        row.Children.Add(label);
        row.Children.Add(editor);

        _overrides.Children.Add(row);
        _overrideRows.Add((use, name, editor));
    }

    private void Warn(string? message) => Dispatcher.UIThread.Post(() =>
    {
        _warning.Text = message ?? string.Empty;
        _warning.IsVisible = message is not null;
    });
}
