using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// Route-B view for <see cref="NewAvailabilityGroupTool"/>. The pre-flight is a gate, not a page: while
/// anything blocks, the view shows only the checks and why DataTray cannot fix them from a connection, and
/// no form at all. Once it passes, the form offers only what can succeed on these instances, and the plan —
/// every statement, per instance — is rebuilt on every change. Nothing here writes.
/// </summary>
internal sealed class NewAvailabilityGroupView : UserControl
{
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,Menlo,monospace");
    private static readonly string[] BackupPreferences = ["SECONDARY", "SECONDARY_ONLY", "PRIMARY", "NONE"];

    private readonly IToolUiContext _context;
    private readonly Dictionary<string, string> _secrets = new();
    private readonly List<(CheckBox Box, string Id)> _secondaryBoxes = [];
    private readonly StackPanel _checks = new() { Spacing = 4 };
    private readonly StackPanel _form = new() { Spacing = 10, IsVisible = false };
    private readonly TextBox _name = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Watermark = "e.g. ag-sales" };
    private readonly ComboBox _clusterType = new() { MinWidth = 360 };
    private readonly ComboBox _backup = new() { MinWidth = 220 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 5022, FormatString = "0", Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Grid _portRow;
    private readonly StackPanel _replicas = new() { Spacing = 6 };
    private readonly StackPanel _databases = new() { Spacing = 2 };
    private readonly TextBlock _invalid = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly StackPanel _warnings = new() { Spacing = 4 };
    private readonly TextBox _script = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = Mono, FontSize = 12, MaxHeight = 320
    };
    private readonly List<(string Name, TextBox Host, CheckBox Sync, CheckBox Auto)> _replicaRows = [];
    private readonly List<(string Name, CheckBox Box)> _databaseRows = [];
    private NewGroupSession? _session;
    private bool _filling;
    private int _generation;

    public NewAvailabilityGroupView(IToolUiContext context)
    {
        _context = context;

        var secondaries = new StackPanel { Spacing = 2 };
        foreach (var c in context.ListConnections())
        {
            var box = new CheckBox { Content = c.Name };
            box.IsCheckedChanged += (_, _) => _ = ReloadAsync();
            secondaries.Children.Add(box);
            _secondaryBoxes.Add((box, c.Id));
        }

        if (_secondaryBoxes.Count == 0)
        {
            secondaries.Children.Add(new TextBlock { Text = "No other saved SQL Server connections. Add one for each instance that should hold a secondary replica.", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        }

        _portRow = Row("Endpoint port", _port);
        _name.TextChanged += (_, _) => Replan();
        _clusterType.SelectionChanged += (_, _) => Replan();
        _backup.SelectionChanged += (_, _) => Replan();
        _port.ValueChanged += (_, _) => Replan();

        _form.Children.Add(Heading("Group"));
        _form.Children.Add(Row("Name", _name));
        _form.Children.Add(Row("Cluster type", _clusterType));
        _form.Children.Add(Row("Backup preference", _backup));
        _form.Children.Add(Note("Only advice: it does nothing unless your backup jobs ask sys.fn_hadr_backup_is_preferred_replica before they run."));
        _form.Children.Add(_portRow);
        _form.Children.Add(Heading("Replicas"));
        _form.Children.Add(_replicas);
        _form.Children.Add(Heading("Databases"));
        _form.Children.Add(_databases);
        _form.Children.Add(Heading("Plan"));
        _form.Children.Add(_invalid);
        _form.Children.Add(_warnings);
        _form.Children.Add(_script);

        Content = new StackPanel
        {
            Spacing = 10,
            MinWidth = 620,
            Children =
            {
                new TextBlock { Text = "The instance this connection reaches becomes the primary replica. Pick the saved connections whose instances hold the secondaries.", TextWrapping = TextWrapping.Wrap },
                Heading("Secondary replicas"),
                secondaries,
                Heading("Pre-flight"),
                _checks,
                _form
            }
        };

        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var generation = ++_generation;
        var ids = _secondaryBoxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToList();
        _context.SetValue(NewAvailabilityGroupTool.SecondariesKey, string.Join(",", ids));
        _context.SetValue(NewAvailabilityGroupTool.ReviewedKey, null);
        _checks.Children.Clear();
        _checks.Children.Add(new TextBlock { Text = "Reading the instances…", Opacity = 0.7 });
        _form.IsVisible = false;

        try
        {
            var session = await NewGroupSession.ReadAsync(
                new ReplicaConnection(_context.Provider, _context.Profile), ids,
                id => _context.OpenConnection(id) is { } c ? new ReplicaConnection(c.Provider, c.Profile) : null,
                CancellationToken.None);
            if (generation == _generation)
            {
                Dispatcher.UIThread.Post(() => Render(session));
            }
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _checks.Children.Clear();
                _checks.Children.Add(CheckRow(CheckLevel.Blocking, "Connection", $"Could not read this instance: {ex.Message}"));
            });
        }
    }

    private void Render(NewGroupSession session)
    {
        _session = session;
        _checks.Children.Clear();
        foreach (var problem in session.Problems)
        {
            _checks.Children.Add(CheckRow(CheckLevel.Blocking, "Connection", problem));
        }

        foreach (var check in session.Preflight.Checks)
        {
            _checks.Children.Add(CheckRow(check.Level, check.Subject, check.Message));
        }

        if (session.Preflight.ClusterTypes.Count == 0)
        {
            _checks.Children.Add(CheckRow(CheckLevel.Blocking, "Cluster type", "No cluster type is possible here: a WSFC group needs every instance in one Windows failover cluster, a read-scale group needs SQL Server 2017 or later on all of them."));
        }

        _form.IsVisible = session.Problems.Count == 0 && session.Preflight.CanProceed;
        if (!_form.IsVisible)
        {
            return;
        }

        _filling = true;
        try
        {
            FillClusterTypes(session.Preflight.ClusterTypes);
            FillBackupPreferences(session.Preflight.BasicOnly);
            _portRow.IsVisible = session.Preflight.CreateEndpoints;
            FillReplicas(session.Instances);
            FillDatabases(session.Preflight);
            foreach (var name in NewAvailabilityGroupPlan.SecretNames(session.Instances.Select(i => i.Name).ToList()))
            {
                _secrets.TryAdd(name, NewAvailabilityGroupPlan.NewPassword());
            }
        }
        finally
        {
            _filling = false;
        }

        Replan();
    }

    private void FillClusterTypes(IReadOnlyList<string> types)
    {
        var current = SelectedTag(_clusterType);
        _clusterType.Items.Clear();
        foreach (var type in types)
        {
            _clusterType.Items.Add(new ComboBoxItem
            {
                Tag = type,
                Content = type == "WSFC"
                    ? "WSFC — high availability, automatic failover through the Windows cluster"
                    : "NONE — read-scale: no cluster, no automatic failover, not high availability"
            });
        }

        // Read-scale is never preselected: choosing it has to be a decision, because it is not HA.
        var keep = types.ToList().IndexOf(current ?? (types.Contains("WSFC") ? "WSFC" : ""));
        _clusterType.SelectedIndex = keep;
    }

    private void FillBackupPreferences(bool basic)
    {
        // A basic group takes no backups on its secondary, so preferring one is not a choice there.
        var offered = basic ? ["PRIMARY"] : BackupPreferences;
        if (_backup.Items.Count == offered.Length)
        {
            return;
        }

        _backup.Items.Clear();
        foreach (var p in offered)
        {
            _backup.Items.Add(new ComboBoxItem { Content = p });
        }

        _backup.SelectedIndex = 0;
    }

    private void FillReplicas(IReadOnlyList<InstanceFacts> instances)
    {
        _replicas.Children.Clear();
        _replicaRows.Clear();
        foreach (var (instance, index) in instances.Select((x, i) => (x, i)))
        {
            // A named instance's server name is HOST\INSTANCE; the endpoint listens on the host.
            var host = new TextBox { Text = instance.Name.Split('\\')[0], Width = 220 };
            var sync = new CheckBox { Content = "Synchronous commit", IsChecked = true };
            var auto = new CheckBox { Content = "Automatic failover", IsChecked = true };
            host.TextChanged += (_, _) => Replan();
            sync.IsCheckedChanged += (_, _) => Replan();
            auto.IsCheckedChanged += (_, _) => Replan();
            _replicaRows.Add((instance.Name, host, sync, auto));
            _replicas.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = $"{instance.Name} ({(index == 0 ? "primary" : "secondary")})", Width = 220, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "endpoint host", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
                    host, sync, auto
                }
            });
        }

        _replicas.Children.Add(Note("The endpoint host must resolve and be reachable from every other instance — a name only the instance itself knows makes the join hang."));
    }

    private void FillDatabases(Preflight preflight)
    {
        _databases.Children.Clear();
        _databaseRows.Clear();
        foreach (var (name, why) in preflight.Databases)
        {
            if (why is null)
            {
                var box = new CheckBox { Content = name };
                box.IsCheckedChanged += (_, _) => Replan();
                _databaseRows.Add((name, box));
                _databases.Children.Add(box);
            }
            else
            {
                // Absent from the choice rather than a disabled box, with the reason where the box would be.
                _databases.Children.Add(new TextBlock { Text = $"{name} — {why}", Opacity = 0.55, Margin = new Thickness(28, 2, 0, 2), TextWrapping = TextWrapping.Wrap });
            }
        }
    }

    private void Replan()
    {
        if (_filling || _session is not { } session)
        {
            return;
        }

        var clusterType = SelectedTag(_clusterType) ?? "";
        var basic = session.Preflight.BasicOnly;
        foreach (var row in _replicaRows)
        {
            // Automatic failover exists only through a Windows cluster, and only between synchronous replicas.
            row.Auto.IsVisible = clusterType == "WSFC";
            row.Auto.IsEnabled = row.Sync.IsChecked == true;
        }

        var choices = new NewGroupChoices(
            _name.Text?.Trim() ?? "",
            clusterType,
            _replicaRows.Select(r => new ReplicaChoice(r.Name, r.Host.Text?.Trim() ?? "", r.Sync.IsChecked == true, r.Auto.IsChecked == true)).ToList(),
            _databaseRows.Where(d => d.Box.IsChecked == true).Select(d => d.Name).ToList(),
            (_backup.SelectedItem as ComboBoxItem)?.Content as string ?? "PRIMARY",
            (int)(_port.Value ?? 5022));

        _context.SetValue(NewAvailabilityGroupTool.ReviewedKey, null);
        _warnings.Children.Clear();
        if (NewAvailabilityGroupPlan.Validate(choices, session.Preflight) is { } invalid)
        {
            _invalid.Text = invalid;
            _invalid.IsVisible = true;
            _script.IsVisible = false;
            return;
        }

        var steps = NewAvailabilityGroupPlan.Plan(choices, session.Instances, session.Preflight.CreateEndpoints, k => _secrets[k]);
        _invalid.IsVisible = false;
        _script.Text = AgStepRunner.Script(steps);
        _script.IsVisible = true;

        var warnings = new List<string>
        {
            "Automatic seeding copies every database to every secondary over the endpoint network, unthrottled. Follow it on the group's dashboard once the group exists.",
            "If a step fails, the steps before it stay done; the run's checklist shows how far it got."
        };
        if (session.Preflight.CreateEndpoints)
        {
            warnings.Insert(0, "The master key passwords in the plan are generated for this run and not stored anywhere. Keep them if you back up master's database master key. The certificate logins' passwords are never used to sign in.");
        }

        if (basic)
        {
            warnings.Add("A basic group cannot be upgraded to a full one later; it has to be dropped and recreated.");
        }

        foreach (var warning in warnings)
        {
            _warnings.Children.Add(new TextBlock { Text = "• " + warning, TextWrapping = TextWrapping.Wrap });
        }

        _context.SetValue(NewAvailabilityGroupTool.ChoicesKey, JsonSerializer.Serialize(choices));
        _context.SetValue(NewAvailabilityGroupTool.SecretsKey, JsonSerializer.Serialize(_secrets));
        _context.SetValue(NewAvailabilityGroupTool.ReviewedKey, AgStepRunner.Script(steps));
    }

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private static Control CheckRow(CheckLevel level, string subject, string message)
    {
        var (word, colour) = level switch
        {
            CheckLevel.Blocking => ("Blocked", "#C62828"),
            CheckLevel.Warning => ("Warning", "#D9822B"),
            _ => ("OK", "#2E7D32")
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("80,160,*") };
        var tag = new TextBlock { Text = word, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse(colour)) };
        var what = new TextBlock { Text = subject, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(what, 1);
        Grid.SetColumn(text, 2);
        grid.Children.Add(tag);
        grid.Children.Add(what);
        grid.Children.Add(text);
        return grid;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock Note(string text) => new() { Text = text, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    private static Grid Row(string label, Control value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*") };
        var name = new TextBlock { Text = label, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(value, 1);
        grid.Children.Add(name);
        grid.Children.Add(value);
        return grid;
    }
}
