using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DataTray.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace DataTray.Providers.MsSql;

/// <summary>
/// The Steps page of <see cref="AgentJobPropertiesView"/> (SE-235): SSMS' job-step list and step editor in
/// one master/detail page. Steps can be added, edited, deleted and reordered — this is the page the whole
/// "manage jobs properly" goal stands on.
/// </summary>
/// <remarks>
/// Reordering is a swap, not a move: <c>sp_update_jobstep</c> has no parameter for changing a step's id
/// (checked against the procedure's own signature), and delete-then-re-add risks losing a step if the second
/// call fails. Exchanging every editable field between two neighbours produces the same visible result — in
/// three phases and one transaction, since step names are unique per job (see <see cref="SwapAsync"/>).
///
/// The subsystem list comes from <c>msdb.dbo.syssubsystems</c> rather than a hard-coded set, because it
/// genuinely differs per server — SSIS is absent on Linux, replication subsystems only appear where
/// replication is installed.
/// </remarks>
internal sealed class AgentJobStepsPage
{
    // on_success_action / on_fail_action, in the order Agent numbers them.
    private static readonly (int Value, string Label)[] Actions =
    [
        (3, "Go to the next step"),
        (1, "Quit the job reporting success"),
        (2, "Quit the job reporting failure"),
        (4, "Go to step…")
    ];

    /// <summary>Name a step parks under for the middle of a swap; never visible outside the transaction.</summary>
    private const string SwapPlaceholder = "__datatray_swap__";

    private readonly NodeInfoContext _context;
    private readonly string _job;

    private readonly ListBox _list = new() { Height = 150 };
    private readonly TextBlock _status = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };

    private readonly TextBox _name = new();
    private readonly ComboBox _subsystem = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _proxy = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _database = new();
    private readonly TextBox _command = new() { AcceptsReturn = true, Height = 150, TextWrapping = TextWrapping.Wrap, FontFamily = FontFamily.Parse("Consolas, Menlo, monospace") };
    private readonly TextBox _outputFile = new();
    private readonly NumericUpDown _retries = new() { Minimum = 0, Maximum = 9999, Width = 100 };
    private readonly NumericUpDown _retryInterval = new() { Minimum = 0, Maximum = 9999, Width = 100 };
    private readonly ComboBox _onSuccess = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _onSuccessStep = new() { Minimum = 1, Maximum = 999, Width = 100 };
    private readonly ComboBox _onFail = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _onFailStep = new() { Minimum = 1, Maximum = 999, Width = 100 };

    private List<Step> _steps = [];
    private bool _loading;

    public AgentJobStepsPage(NodeInfoContext context)
    {
        _context = context;
        _job = context.Node.Name;
        Control = Build();
        _ = ReloadAsync(select: 0);
    }

    public Control Control { get; }

    private sealed record Step(
        int Id, string Name, string Subsystem, string Command, string Database, string OutputFile,
        int Retries, int RetryInterval, int OnSuccess, int OnSuccessStep, int OnFail, int OnFailStep,
        string Proxy, string LastOutcome);

    private Control Build()
    {
        _list.SelectionChanged += (_, _) => ShowSelected();
        _subsystem.ItemsSource = new[] { "TSQL" };
        _onSuccess.ItemsSource = Actions.Select(a => a.Label).ToList();
        _onFail.ItemsSource = Actions.Select(a => a.Label).ToList();
        _onSuccess.SelectionChanged += (_, _) => SyncStepPickers();
        _onFail.SelectionChanged += (_, _) => SyncStepPickers();

        var add = new Button { Content = "New" };
        var delete = new Button { Content = "Delete" };
        var up = new Button { Content = "↑" };
        var down = new Button { Content = "↓" };
        var save = new Button { Content = "Save step", HorizontalAlignment = HorizontalAlignment.Right };

        add.Click += (_, _) => NewStep();
        delete.Click += async (_, _) => await GuardAsync(delete, DeleteSelectedAsync);
        up.Click += async (_, _) => await GuardAsync(up, () => SwapAsync(-1));
        down.Click += async (_, _) => await GuardAsync(down, () => SwapAsync(+1));
        save.Click += async (_, _) => await GuardAsync(save, SaveSelectedAsync);

        var editor = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Labelled("Step name", _name),
                Labelled("Type", _subsystem),
                Labelled("Run as", _proxy),
                Labelled("Database", _database),
                Labelled("Command", _command),
                Labelled("Output file", _outputFile),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 16,
                    Children = { Labelled("Retry attempts", _retries), Labelled("Retry interval (min)", _retryInterval) }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 16,
                    Children = { Labelled("On success", _onSuccess), Labelled("Step", _onSuccessStep) }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 16,
                    Children = { Labelled("On failure", _onFail), Labelled("Step", _onFailStep) }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { _status, save }
                }
            }
        };

        var page = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { add, delete, up, down }
                },
                editor
            }
        };

        // A job with no steps never reaches ShowSelected, so hide the jump-to-step boxes up front.
        SyncStepPickers();
        return new ScrollViewer { Content = page, Padding = new Thickness(12) };
    }

    private static Control Labelled(string label, Control editor) => new StackPanel
    {
        Spacing = 2,
        Children = { new TextBlock { Text = label, Opacity = 0.65 }, editor }
    };

    // Run a button's action with the button disabled, and put whatever went wrong where the user can read it.
    private async Task GuardAsync(Button button, Func<Task> action)
    {
        button.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // ── Load ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task ReloadAsync(int select)
    {
        try
        {
            await using var connection = await OpenAsync();

            var subsystems = await ScalarListAsync(connection,
                "SELECT subsystem FROM msdb.dbo.syssubsystems ORDER BY subsystem");
            var proxies = await ScalarListAsync(connection,
                "SELECT name FROM msdb.dbo.sysproxies WHERE enabled = 1 ORDER BY name");

            await using var command = new SqlCommand(
                """
                SELECT s.step_id, s.step_name, s.subsystem, s.command, ISNULL(s.database_name, ''),
                       ISNULL(s.output_file_name, ''), s.retry_attempts, s.retry_interval,
                       -- on_success_action and on_fail_action are tinyint while every other number here
                       -- is int; cast so one reader call type covers the row.
                       CAST(s.on_success_action AS int), s.on_success_step_id,
                       CAST(s.on_fail_action AS int), s.on_fail_step_id,
                       ISNULL(p.name, ''), s.last_run_outcome
                FROM msdb.dbo.sysjobsteps s
                JOIN msdb.dbo.sysjobs j ON j.job_id = s.job_id
                LEFT JOIN msdb.dbo.sysproxies p ON p.proxy_id = s.proxy_id
                WHERE j.name = @name
                ORDER BY s.step_id
                """, connection);
            command.Parameters.AddWithValue("name", _job);

            var steps = new List<Step>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    steps.Add(new Step(
                        reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7),
                        reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
                        reader.GetString(12), AgentJobStatus.OutcomeName(reader.GetInt32(13))));
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                _steps = steps;
                // "(default)" is the empty proxy — the step runs as the job owner, which is the usual case.
                _proxy.ItemsSource = new[] { "(default)" }.Concat(proxies).ToList();
                _subsystem.ItemsSource = subsystems;
                _list.ItemsSource = steps
                    .Select(s => $"{s.Id} — {s.Name}  ({s.Subsystem}, last: {s.LastOutcome})")
                    .ToList();
                _list.SelectedIndex = steps.Count == 0 ? -1 : Math.Clamp(select, 0, steps.Count - 1);
                _status.Text = steps.Count == 0 ? "This job has no steps." : "";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => _status.Text = ex.Message);
        }
    }

    private void ShowSelected()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _steps.Count)
        {
            return;
        }

        _loading = true;
        var step = _steps[_list.SelectedIndex];
        _name.Text = step.Name;
        _subsystem.SelectedItem = step.Subsystem;
        _proxy.SelectedItem = string.IsNullOrEmpty(step.Proxy) ? "(default)" : step.Proxy;
        _database.Text = step.Database;
        _command.Text = step.Command;
        _outputFile.Text = step.OutputFile;
        _retries.Value = step.Retries;
        _retryInterval.Value = step.RetryInterval;
        _onSuccess.SelectedIndex = IndexOfAction(step.OnSuccess);
        _onSuccessStep.Value = Math.Max(1, step.OnSuccessStep);
        _onFail.SelectedIndex = IndexOfAction(step.OnFail);
        _onFailStep.Value = Math.Max(1, step.OnFailStep);
        _loading = false;

        SyncStepPickers();
    }

    private void NewStep()
    {
        _loading = true;
        _list.SelectedIndex = -1;
        _name.Text = $"Step {_steps.Count + 1}";
        _subsystem.SelectedItem = "TSQL";
        _proxy.SelectedItem = "(default)";
        _database.Text = "master";
        _command.Text = "";
        _outputFile.Text = "";
        _retries.Value = 0;
        _retryInterval.Value = 0;
        _onSuccess.SelectedIndex = 0;
        _onFail.SelectedIndex = 2;
        _loading = false;

        SyncStepPickers();
        _status.Text = "New step — Save step adds it at the end.";
    }

    // The "Go to step" number only means anything for that action; hide it the rest of the time.
    private void SyncStepPickers()
    {
        if (_loading)
        {
            return;
        }

        _onSuccessStep.IsVisible = Action(_onSuccess) == 4;
        _onFailStep.IsVisible = Action(_onFail) == 4;
    }

    private static int IndexOfAction(int value) =>
        Math.Max(0, Array.FindIndex(Actions, a => a.Value == value));

    private static int Action(ComboBox box) =>
        Actions[Math.Clamp(box.SelectedIndex, 0, Actions.Length - 1)].Value;

    // ── Write ────────────────────────────────────────────────────────────────────────────────────────

    private async Task SaveSelectedAsync()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _status.Text = "A step needs a name.";
            return;
        }

        var isNew = _list.SelectedIndex < 0;
        var proxy = _proxy.SelectedItem as string;
        var settings =
            $"@step_name = N'{Escape(_name.Text)}'" +
            $", @subsystem = N'{Escape(_subsystem.SelectedItem as string ?? "TSQL")}'" +
            $", @command = N'{Escape(_command.Text ?? string.Empty)}'" +
            $", @database_name = N'{Escape(_database.Text ?? string.Empty)}'" +
            $", @output_file_name = N'{Escape(_outputFile.Text ?? string.Empty)}'" +
            $", @retry_attempts = {(int)(_retries.Value ?? 0)}" +
            $", @retry_interval = {(int)(_retryInterval.Value ?? 0)}" +
            $", @on_success_action = {Action(_onSuccess)}" +
            $", @on_success_step_id = {(Action(_onSuccess) == 4 ? (int)(_onSuccessStep.Value ?? 1) : 0)}" +
            $", @on_fail_action = {Action(_onFail)}" +
            $", @on_fail_step_id = {(Action(_onFail) == 4 ? (int)(_onFailStep.Value ?? 1) : 0)}" +
            (proxy is null or "(default)" ? "" : $", @proxy_name = N'{Escape(proxy)}'");

        var sql = isNew
            ? $"EXEC msdb.dbo.sp_add_jobstep @job_name = N'{Escape(_job)}', {settings}"
            : $"EXEC msdb.dbo.sp_update_jobstep @job_name = N'{Escape(_job)}', "
              + $"@step_id = {_steps[_list.SelectedIndex].Id}, {settings}";

        await ExecuteAsync(sql);
        _status.Text = isNew ? "Step added." : "Step saved.";
        await ReloadAsync(select: isNew ? _steps.Count : _list.SelectedIndex);
    }

    private async Task DeleteSelectedAsync()
    {
        if (_list.SelectedIndex < 0)
        {
            return;
        }

        // Agent renumbers the steps after the one removed, so reload rather than patching the list.
        var index = _list.SelectedIndex;
        await ExecuteAsync($"EXEC msdb.dbo.sp_delete_jobstep @job_name = N'{Escape(_job)}', "
                           + $"@step_id = {_steps[index].Id}");
        _status.Text = "Step deleted.";
        await ReloadAsync(select: Math.Max(0, index - 1));
    }

    private async Task SwapAsync(int direction)
    {
        var index = _list.SelectedIndex;
        var other = index + direction;
        if (index < 0 || other < 0 || other >= _steps.Count)
        {
            return;
        }

        // Three phases, not two: step names are unique within a job, so writing B's name onto A while B still
        // holds it is rejected outright (Agent error 14261). A parks under a placeholder name first. All of it
        // in one transaction, because a half-done swap would leave a step called __datatray_swap__ behind.
        var a = _steps[index];
        var b = _steps[other];
        await ExecuteAsync(
            "SET XACT_ABORT ON;\nBEGIN TRANSACTION;\n"
            + $"EXEC msdb.dbo.sp_update_jobstep @job_name = N'{Escape(_job)}', @step_id = {a.Id}"
            + $", @step_name = N'{SwapPlaceholder}';\n"
            + UpdateWith(b.Id, a) + ";\n"
            + UpdateWith(a.Id, b) + ";\n"
            + "COMMIT;");

        _status.Text = "Step moved.";
        await ReloadAsync(select: other);
    }

    private string UpdateWith(int stepId, Step from) =>
        $"EXEC msdb.dbo.sp_update_jobstep @job_name = N'{Escape(_job)}', @step_id = {stepId}" +
        $", @step_name = N'{Escape(from.Name)}'" +
        $", @subsystem = N'{Escape(from.Subsystem)}'" +
        $", @command = N'{Escape(from.Command)}'" +
        $", @database_name = N'{Escape(from.Database)}'" +
        $", @output_file_name = N'{Escape(from.OutputFile)}'" +
        $", @retry_attempts = {from.Retries}" +
        $", @retry_interval = {from.RetryInterval}" +
        $", @on_success_action = {from.OnSuccess}" +
        $", @on_success_step_id = {from.OnSuccessStep}" +
        $", @on_fail_action = {from.OnFail}" +
        $", @on_fail_step_id = {from.OnFailStep}" +
        (string.IsNullOrEmpty(from.Proxy) ? "" : $", @proxy_name = N'{Escape(from.Proxy)}'");

    private Task ExecuteAsync(string sql) =>
        _context.Provider.ExecuteDdlAsync(_context.Profile, sql, CancellationToken.None);

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────────

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_context.Profile.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<List<string>> ScalarListAsync(SqlConnection connection, string sql)
    {
        var values = new List<string>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
