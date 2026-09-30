using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// Route-B view for <see cref="FailoverAvailabilityGroupTool"/>: pick the target replica, map the replicas
/// the plan runs on to saved connections, and read the exact plan — every statement and the instance it runs
/// on — before Run is worth pressing. A plan that can lose data, or takes the group offline, asks for the
/// group name to be typed. Nothing here writes; the view only reads state and fills in the tool's inputs.
/// </summary>
internal sealed class FailoverAvailabilityGroupView : UserControl
{
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,Menlo,monospace");

    private readonly IToolUiContext _context;
    private readonly string _group;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _target = new() { MinWidth = 220 };
    private readonly ConnectionRow _primaryRow;
    private readonly ConnectionRow _targetRow;
    private readonly TextBlock _problems = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#D9822B")) };
    private readonly Border _banner = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 8) };
    private readonly TextBlock _bannerText = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly StackPanel _warnings = new() { Spacing = 4 };
    private readonly TextBox _script = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = Mono, FontSize = 12,
        MaxHeight = 280
    };
    private readonly StackPanel _confirmPanel = new() { Spacing = 4, IsVisible = false };
    private readonly TextBox _confirm = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
    private bool _filling;
    private int _generation;

    public FailoverAvailabilityGroupView(IToolUiContext context)
    {
        _context = context;
        _group = context.Node?.Name ?? string.Empty;
        _banner.Child = _bannerText;
        _primaryRow = new ConnectionRow(context.ListConnections(), OnInputChanged);
        _targetRow = new ConnectionRow(context.ListConnections(), OnInputChanged);

        _target.SelectionChanged += (_, _) => OnInputChanged();
        _confirm.TextChanged += (_, _) => context.SetValue(FailoverAvailabilityGroupTool.ConfirmKey, _confirm.Text);

        var recheck = new Button { Content = "Re-check state", HorizontalAlignment = HorizontalAlignment.Left };
        recheck.Click += (_, _) => _ = ReloadAsync();

        _confirmPanel.Children.Add(new TextBlock { Text = $"Type the group name ({_group}) to confirm:", TextWrapping = TextWrapping.Wrap });
        _confirmPanel.Children.Add(_confirm);

        Content = new StackPanel
        {
            Spacing = 10,
            MinWidth = 560,
            Children =
            {
                _summary,
                Row("Fail over to", _target),
                _primaryRow.Panel,
                _targetRow.Panel,
                _problems,
                _banner,
                _warnings,
                _script,
                _confirmPanel,
                recheck
            }
        };

        _ = ReloadAsync();
    }

    private void OnInputChanged()
    {
        if (!_filling)
        {
            _ = ReloadAsync();
        }
    }

    private async Task ReloadAsync()
    {
        var generation = ++_generation;
        var target = (_target.SelectedItem as ComboBoxItem)?.Content as string;
        Show(FailoverKind.Refused, "Reading the group's state…");
        // The reviewed script is cleared first, so a Run pressed mid-reload cannot confirm a stale plan.
        _context.SetValue(FailoverAvailabilityGroupTool.ReviewedKey, null);

        try
        {
            var launch = new ReplicaConnection(_context.Provider, _context.Profile);
            var session = await FailoverSession.ReadAsync(launch, _group,
                id => _context.OpenConnection(id) is { } c ? new ReplicaConnection(c.Provider, c.Profile) : null,
                _primaryRow.SelectedId, _targetRow.SelectedId, target, CancellationToken.None);
            if (generation != _generation)
            {
                return;
            }

            Dispatcher.UIThread.Post(() => Render(session, target));
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Show(FailoverKind.Refused, $"Could not read the group's state: {ex.Message}"));
        }
    }

    private void Render(FailoverSession session, string? target)
    {
        var t = session.Topology;
        _summary.Text = $"{t.Group} — cluster type {t.ClusterType?.ToUpperInvariant() ?? "unknown"}, primary {t.Primary ?? "unknown"}. "
                        + $"This connection is {session.LocalReplica}.";

        var candidates = t.Replicas.Select(r => r.Name)
            .Where(n => t.Primary is null || !string.Equals(n, t.Primary, StringComparison.OrdinalIgnoreCase)).ToList();
        _filling = true;
        try
        {
            if (!candidates.SequenceEqual(_target.Items.OfType<ComboBoxItem>().Select(i => i.Content as string)))
            {
                _target.Items.Clear();
                foreach (var name in candidates)
                {
                    _target.Items.Add(new ComboBoxItem { Content = name });
                }
            }

            if (_target.SelectedItem is null && candidates.Count > 0)
            {
                // Launched on a secondary: that is almost always the replica meant to take over.
                var preferred = candidates.FindIndex(n => string.Equals(n, session.LocalReplica, StringComparison.OrdinalIgnoreCase));
                _target.SelectedIndex = Math.Max(preferred, 0);
                target = candidates[_target.SelectedIndex];
            }

            _primaryRow.Show(t.Primary, "Primary", session);
            _targetRow.Show(target, "Target", session);
        }
        finally
        {
            _filling = false;
        }

        _problems.Text = string.Join(Environment.NewLine, session.Problems);
        _problems.IsVisible = session.Problems.Count > 0;

        if (target is null)
        {
            Show(FailoverKind.Refused, $"{t.Group} has no secondary replica to fail over to.");
            return;
        }

        if (session.ConnectionFor(target) is null && t.ClusterType?.ToUpperInvariant() != "EXTERNAL")
        {
            Show(FailoverKind.Refused, $"Pick the saved connection to {target} — the failover statement runs on that instance. No connection to it yet? Add one in Manage Connections, then re-check.");
            return;
        }

        var plan = AvailabilityGroupFailover.Plan(t, target);
        _context.SetValue(FailoverAvailabilityGroupTool.TargetKey, target);
        _context.SetValue(FailoverAvailabilityGroupTool.PrimaryConnectionKey, _primaryRow.SelectedId);
        _context.SetValue(FailoverAvailabilityGroupTool.TargetConnectionKey, _targetRow.SelectedId);

        var missing = plan.Steps.Select(s => s.Replica).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => session.ConnectionFor(r) is null).ToList();
        if (plan.Kind != FailoverKind.Refused && missing.Count > 0)
        {
            Show(FailoverKind.Refused, $"This plan also runs on {string.Join(", ", missing)}. Pick a saved connection to it above.", plan);
            return;
        }

        var headline = plan.Kind switch
        {
            FailoverKind.Planned => $"Planned failover to {target}. No data loss.",
            FailoverKind.Forced => $"FORCED FAILOVER TO {target} — COMMITTED TRANSACTIONS CAN BE LOST.",
            FailoverKind.ReadScale => $"Read-scale failover to {target}: {plan.Steps.Count} steps on two instances. The group is offline for part of it.",
            _ => plan.RefusalReason ?? "DataTray will not run this failover."
        };
        Show(plan.Kind, headline, plan);
        if (plan.Kind != FailoverKind.Refused)
        {
            _context.SetValue(FailoverAvailabilityGroupTool.ReviewedKey, plan.Script());
        }
    }

    private void Show(FailoverKind kind, string headline, FailoverPlan? plan = null)
    {
        var colour = Color.Parse(kind switch
        {
            FailoverKind.Planned => "#2E7D32",
            FailoverKind.Forced => "#C62828",
            FailoverKind.ReadScale => "#D9822B",
            _ => "#808080"
        });
        _banner.BorderBrush = new SolidColorBrush(colour);
        _banner.Background = new SolidColorBrush(colour, 0.14);
        _bannerText.Text = headline;

        _warnings.Children.Clear();
        foreach (var warning in plan?.Warnings ?? [])
        {
            _warnings.Children.Add(new TextBlock { Text = "• " + warning, TextWrapping = TextWrapping.Wrap });
        }

        var script = plan?.ExternalCommands ?? (plan is { Steps.Count: > 0 } ? plan.Script() : null);
        _script.Text = script;
        _script.IsVisible = script is not null;
        _confirmPanel.IsVisible = kind != FailoverKind.Refused && plan is { NeedsTypedConfirmation: true };
    }

    private static Grid Row(string label, Control value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*") };
        var name = new TextBlock { Text = label, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(value, 1);
        grid.Children.Add(name);
        grid.Children.Add(value);
        return grid;
    }

    /// <summary>"Primary (&lt;replica&gt;)": "this connection" when the replica is the one the tool was
    /// launched on, otherwise a pick from the user's saved SQL Server connections.</summary>
    private sealed class ConnectionRow
    {
        private readonly TextBlock _label = new() { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _local = new() { Text = "this connection", VerticalAlignment = VerticalAlignment.Center };
        private readonly ComboBox _box = new() { MinWidth = 220 };

        public ConnectionRow(IReadOnlyList<ToolConnectionInfo> connections, Action changed)
        {
            foreach (var c in connections)
            {
                _box.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });
            }

            _box.SelectionChanged += (_, _) => changed();
            var value = new Panel { Children = { _local, _box } };
            Panel = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), IsVisible = false };
            Grid.SetColumn(value, 1);
            Panel.Children.Add(_label);
            Panel.Children.Add(value);
        }

        public Grid Panel { get; }

        public string? SelectedId => (_box.SelectedItem as ComboBoxItem)?.Tag as string;

        public void Show(string? replica, string role, FailoverSession session)
        {
            Panel.IsVisible = replica is not null;
            if (replica is null)
            {
                return;
            }

            var isLocal = string.Equals(replica, session.LocalReplica, StringComparison.OrdinalIgnoreCase);
            _label.Text = $"{role} ({replica})";
            _local.IsVisible = isLocal;
            _box.IsVisible = !isLocal;
            if (!isLocal && _box.Items.Count == 0)
            {
                _local.Text = "no saved SQL Server connections — add one in Manage Connections";
                _local.IsVisible = true;
            }
        }
    }
}
