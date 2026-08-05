using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DataTray.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace DataTray.Providers.MsSql;

/// <summary>
/// The Alerts page of <see cref="AgentJobPropertiesView"/> (SE-235): the alerts that respond by running this
/// job, and the editor for them.
/// </summary>
/// <remarks>
/// An alert is its own object that happens to point at a job, so creating one here sets <c>@job_name</c> and
/// deleting one deletes the alert — there is no "detach" the way a schedule has, because an alert without a
/// response is not something this page can express.
///
/// Two of the three alert kinds are here: a SQL Server event (error number or severity, optionally narrowed
/// to a database and a phrase) and a performance condition. WMI alerts are not, and deliberately: msdb keeps
/// their namespace and query outside <c>sysalerts</c>, so the page could write one and then never read it
/// back — an editor that silently forgets what you typed is worse than one that says it does not do this.
/// </remarks>
internal sealed class AgentJobAlertsPage : IJobPage
{
    private static readonly string[] Kinds = ["SQL Server event", "SQL Server performance condition"];

    private readonly NodeInfoContext _context;
    private readonly string _job;
    private readonly Action<string> _report;

    private readonly SelectTable _table = new(
        ["Name", "Enabled", "Responds to", "Database"],
        [180, 70, 0, 130]);

    private readonly TextBox _name = new();
    private readonly CheckBox _enabled = new() { Content = "Enabled" };
    private readonly ComboBox _kind = new() { Width = 260, ItemsSource = Kinds, SelectedIndex = 0 };
    private readonly ComboBox _database = new() { Width = 220 };
    private readonly NumericUpDown _messageId = new() { Minimum = 0, Maximum = 2147483647, Width = 140 };
    private readonly NumericUpDown _severity = new() { Minimum = 0, Maximum = 25, Width = 140 };
    private readonly TextBox _keyword = new();
    private readonly TextBox _performance = new() { PlaceholderText = "Object|Counter|Instance|>|Value" };
    private readonly NumericUpDown _delay = new() { Minimum = 0, Maximum = 999999, Width = 140 };
    private readonly TextBox _message = new() { AcceptsReturn = true, Height = 56, TextWrapping = TextWrapping.Wrap };

    private Control _eventRows = null!;
    private Control _performanceRow = null!;

    private List<Alert> _alerts = [];
    private bool _loading;

    public AgentJobAlertsPage(NodeInfoContext context, Action<string> report)
    {
        _context = context;
        _job = context.Node.Name;
        _report = report;
        Control = Build();
        _ = ReloadAsync(select: 0);
    }

    public Control Control { get; }

    public string ActionLabel => "Save alert";

    private sealed record Alert(
        string Name, bool Enabled, int MessageId, int Severity, string Database, string Keyword,
        string Performance, int Delay, string Message);

    private Control Build()
    {
        _table.SelectionChanged += ShowSelected;
        _kind.SelectionChanged += (_, _) => SyncVisibility();

        var add = new Button { Content = "New" };
        var delete = new Button { Content = "Delete" };
        add.Click += (_, _) => NewAlert();
        delete.Click += async (_, _) => await GuardAsync(delete, DeleteSelectedAsync);

        _eventRows = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                FormBits.Row("Error number / severity (0 = ignore)", _messageId, _severity),
                FormBits.Labelled("Database", _database),
                FormBits.Labelled("Only when the message contains", _keyword)
            }
        };
        _performanceRow = FormBits.Labelled("Condition", _performance);

        var form = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                FormBits.Section("Alert"),
                FormBits.Labelled("Name", _name),
                _enabled,
                FormBits.Labelled("Type", _kind),
                _eventRows,
                _performanceRow,
                FormBits.Section("Response"),
                FormBits.Row("Delay between responses (seconds)", _delay),
                FormBits.Labelled("Notification message", _message)
            }
        };

        var page = FormBits.Page(
            FormBits.Section("Alerts that run this job"),
            _table.Control,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { add, delete } },
            form);

        // An empty list means neither ShowSelected nor NewAlert has run, so settle the conditional rows here.
        SyncVisibility();
        return page;
    }

    private async Task GuardAsync(Button button, Func<Task> action)
    {
        button.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _report(ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task ReloadAsync(int select)
    {
        try
        {
            await using var connection = await OpenAsync();

            var databases = new List<string> { "(all databases)" };
            await using (var list = new SqlCommand(
                             "SELECT name FROM sys.databases WHERE state = 0 ORDER BY name", connection))
            await using (var reader = await list.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    databases.Add(reader.GetString(0));
                }
            }

            await using var command = new SqlCommand(
                """
                SELECT a.name, a.enabled, a.message_id, a.severity, ISNULL(a.database_name, ''),
                       ISNULL(a.event_description_keyword, ''), ISNULL(a.performance_condition, ''),
                       a.delay_between_responses, ISNULL(a.notification_message, '')
                FROM msdb.dbo.sysalerts a
                JOIN msdb.dbo.sysjobs j ON j.job_id = a.job_id
                WHERE j.name = @name
                ORDER BY a.name
                """, connection);
            command.Parameters.AddWithValue("name", _job);

            var alerts = new List<Alert>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    alerts.Add(new Alert(
                        reader.GetString(0), reader.GetByte(1) == 1, reader.GetInt32(2), reader.GetInt32(3),
                        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7),
                        reader.GetString(8)));
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                _alerts = alerts;
                _database.ItemsSource = databases;
            });

            _table.Fill(alerts.Select(Row).ToList(), "No alert runs this job.");
            Dispatcher.UIThread.Post(() =>
                _table.SelectedIndex = alerts.Count == 0 ? -1 : Math.Clamp(select, 0, alerts.Count - 1));
        }
        catch (Exception ex)
        {
            _report(ex.Message);
        }
    }

    private static Cell[] Row(Alert a)
    {
        var cause = !string.IsNullOrEmpty(a.Performance) ? a.Performance
            : a.MessageId > 0 ? $"error {a.MessageId}"
            : a.Severity > 0 ? $"severity {a.Severity}"
            : "any error";
        return
        [
            new(a.Name),
            new(a.Enabled ? "yes" : "no", a.Enabled ? null : AgentJobStepsPage.OutcomeBrush("canceled")),
            new(cause),
            new(string.IsNullOrEmpty(a.Database) ? "all" : a.Database)
        ];
    }

    private void ShowSelected()
    {
        if (_table.SelectedIndex < 0 || _table.SelectedIndex >= _alerts.Count)
        {
            return;
        }

        _loading = true;
        var a = _alerts[_table.SelectedIndex];
        _name.Text = a.Name;
        _enabled.IsChecked = a.Enabled;
        _kind.SelectedIndex = string.IsNullOrEmpty(a.Performance) ? 0 : 1;
        _messageId.Value = a.MessageId;
        _severity.Value = a.Severity;
        _database.SelectedItem = string.IsNullOrEmpty(a.Database) ? "(all databases)" : a.Database;
        _keyword.Text = a.Keyword;
        _performance.Text = a.Performance;
        _delay.Value = a.Delay;
        _message.Text = a.Message;
        _loading = false;

        SyncVisibility();
    }

    private void NewAlert()
    {
        _loading = true;
        _table.SelectedIndex = -1;
        _name.Text = $"{_job} alert";
        _enabled.IsChecked = true;
        _kind.SelectedIndex = 0;
        _messageId.Value = 0;
        _severity.Value = 16;
        _database.SelectedItem = "(all databases)";
        _keyword.Text = "";
        _performance.Text = "";
        _delay.Value = 0;
        _message.Text = "";
        _loading = false;

        SyncVisibility();
        _report("New alert — Save alert creates it pointing at this job.");
    }

    private void SyncVisibility()
    {
        if (_loading)
        {
            return;
        }

        _eventRows.IsVisible = _kind.SelectedIndex == 0;
        _performanceRow.IsVisible = _kind.SelectedIndex == 1;
    }

    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _report("An alert needs a name.");
            return;
        }

        var isPerformance = _kind.SelectedIndex == 1;
        if (isPerformance && string.IsNullOrWhiteSpace(_performance.Text))
        {
            _report("A performance alert needs a condition.");
            return;
        }

        var database = _database.SelectedItem as string;

        // Both have to go, with the unused one explicitly zeroed. Sending only the one you mean leaves the
        // other at NULL rather than at its stored value, and sp_verify_alert then rejects the call with
        // "supply either a non-zero message ID, non-zero severity, ..." — which reads like the opposite
        // complaint. An alert switching from severity to error number is exactly that case.
        var messageId = (int)(_messageId.Value ?? 0);
        var severity = (int)(_severity.Value ?? 0);
        var cause = isPerformance
            ? $", @message_id = 0, @severity = 0, @performance_condition = N'{Escape(_performance.Text!)}'"
            : messageId > 0
                ? $", @message_id = {messageId}, @severity = 0"
                : $", @message_id = 0, @severity = {severity}";

        var settings =
            $"@enabled = {(_enabled.IsChecked == true ? 1 : 0)}" +
            cause +
            $", @delay_between_responses = {(int)(_delay.Value ?? 0)}" +
            $", @notification_message = N'{Escape(_message.Text ?? string.Empty)}'" +
            (isPerformance || database is null or "(all databases)"
                ? ""
                : $", @database_name = N'{Escape(database)}'") +
            (isPerformance || string.IsNullOrWhiteSpace(_keyword.Text)
                ? ""
                : $", @event_description_keyword = N'{Escape(_keyword.Text)}'");

        var isNew = _table.SelectedIndex < 0;
        var add = $"EXEC msdb.dbo.sp_add_alert @name = N'{Escape(_name.Text)}', {settings}"
                  + $", @job_name = N'{Escape(_job)}'";

        string sql;
        if (isNew)
        {
            sql = add;
        }
        else
        {
            var current = _alerts[_table.SelectedIndex];
            var wasPerformance = !string.IsNullOrEmpty(current.Performance);

            // Changing what an alert responds to is the one thing sp_update_alert will not do: handed a
            // performance condition it keeps the old message id and reports success, so the change looks
            // applied and is not. Recreating is the only honest way, in a transaction so a failure cannot
            // leave the alert deleted.
            sql = wasPerformance != isPerformance
                ? "SET XACT_ABORT ON;\nBEGIN TRANSACTION;\n"
                  + $"EXEC msdb.dbo.sp_delete_alert @name = N'{Escape(current.Name)}';\n{add};\nCOMMIT;"
                : $"EXEC msdb.dbo.sp_update_alert @name = N'{Escape(current.Name)}'"
                  + $", @new_name = N'{Escape(_name.Text)}', {settings}, @job_name = N'{Escape(_job)}'";
        }

        await ExecuteAsync(sql);
        _report(isNew ? "Alert created." : "Alert saved.");
        await ReloadAsync(select: isNew ? _alerts.Count : _table.SelectedIndex);
    }

    private async Task DeleteSelectedAsync()
    {
        if (_table.SelectedIndex < 0)
        {
            return;
        }

        var index = _table.SelectedIndex;
        await ExecuteAsync($"EXEC msdb.dbo.sp_delete_alert @name = N'{Escape(_alerts[index].Name)}'");
        _report("Alert deleted.");
        await ReloadAsync(select: Math.Max(0, index - 1));
    }

    private Task ExecuteAsync(string sql) =>
        _context.Provider.ExecuteDdlAsync(_context.Profile, sql, CancellationToken.None);

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_context.Profile.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
