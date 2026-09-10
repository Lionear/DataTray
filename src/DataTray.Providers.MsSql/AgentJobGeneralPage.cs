using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DataTray.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace DataTray.Providers.MsSql;

/// <summary>
/// The General page of <see cref="AgentJobPropertiesView"/>: what the job is, the four things about it that
/// can be changed, and the read-only status underneath.
/// </summary>
internal sealed class AgentJobGeneralPage : IJobPage
{
    private readonly NodeInfoContext _context;
    private readonly string _job;
    private readonly Action<string> _report;

    private readonly TextBox _name = new();
    private readonly CheckBox _enabled = new() { Content = "Enabled" };
    private readonly ComboBox _category = new();
    private readonly ComboBox _owner = new();
    private readonly TextBox _description = new() { AcceptsReturn = true, Height = 54, TextWrapping = TextWrapping.Wrap };
    private readonly PropPage _status = new();

    public AgentJobGeneralPage(NodeInfoContext context, Action<string> report)
    {
        _context = context;
        _job = context.Node.Name;
        _report = report;

        _status.Row("Created", "created");
        _status.Row("Last modified", "modified");
        _status.Row("Last run", "lastRun");
        _status.Row("Next run", "nextRun");

        Control = FormBits.Page(
            FormBits.Section("Job"),
            FormBits.Pair(FormBits.Labelled("Name", _name), FormBits.Labelled("Owner", _owner)),
            FormBits.Pair(FormBits.Labelled("Category", _category),
                new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { _enabled } }),
            FormBits.Labelled("Description", _description),
            FormBits.Section("Status"),
            _status.Stack);

        _ = LoadAsync();
    }

    public Control Control { get; }

    public string ActionLabel => "Save";

    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _report("A job needs a name.");
            return;
        }

        // sp_update_job ignores what it is not given, so only the four this page owns are sent. A rename
        // goes through @new_name, which is why the name is editable here at all.
        var sql = $"EXEC msdb.dbo.sp_update_job @job_name = N'{Escape(_job)}'"
                  + (_name.Text == _job ? "" : $", @new_name = N'{Escape(_name.Text)}'")
                  + $", @enabled = {(_enabled.IsChecked == true ? 1 : 0)}"
                  + $", @description = N'{Escape(_description.Text ?? string.Empty)}'"
                  + (_category.SelectedItem is string category ? $", @category_name = N'{Escape(category)}'" : "")
                  + (_owner.SelectedItem is string owner ? $", @owner_login_name = N'{Escape(owner)}'" : "");

        await _context.Provider.ExecuteDdlAsync(_context.Profile, sql, CancellationToken.None);
        _report(_name.Text == _job ? "Saved." : $"Saved — the job is now called {_name.Text}.");
    }

    private async Task LoadAsync()
    {
        try
        {
            await using var connection = new SqlConnection(_context.Profile.ConnectionString);
            await connection.OpenAsync();

            var categories = await ListAsync(connection,
                "SELECT name FROM msdb.dbo.syscategories WHERE category_class = 1 ORDER BY name");
            var owners = await ListAsync(connection,
                "SELECT name FROM sys.server_principals WHERE type IN ('S','U','G') AND name NOT LIKE '##%' ORDER BY name");

            await using var command = new SqlCommand(
                """
                SELECT j.enabled, ISNULL(j.description, ''), ISNULL(c.name, ''),
                       ISNULL(SUSER_SNAME(j.owner_sid), ''), j.date_created, j.date_modified,
                       s.last_run_outcome, s.last_run_date, s.last_run_time,
                       n.next_run_date, n.next_run_time
                FROM msdb.dbo.sysjobs j
                LEFT JOIN msdb.dbo.syscategories c ON c.category_id = j.category_id
                LEFT JOIN msdb.dbo.sysjobservers s ON s.job_id = j.job_id
                OUTER APPLY (
                    SELECT TOP 1 js.next_run_date, js.next_run_time
                    FROM msdb.dbo.sysjobschedules js
                    WHERE js.job_id = j.job_id AND js.next_run_date > 0
                    ORDER BY js.next_run_date, js.next_run_time
                ) n
                WHERE j.name = @name
                """, connection);
            command.Parameters.AddWithValue("name", _job);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                _report("This job no longer exists.");
                return;
            }

            var enabled = reader.GetByte(0) == 1;
            var description = reader.GetString(1);
            var category = reader.GetString(2);
            var owner = reader.GetString(3);
            var created = reader.GetDateTime(4);
            var modified = reader.GetDateTime(5);
            var lastRun = reader.IsDBNull(7)
                ? null
                : AgentJobStatus.Timestamp(reader.GetInt32(7), reader.IsDBNull(8) ? 0 : reader.GetInt32(8));
            var outcome = reader.IsDBNull(6) ? "unknown" : AgentJobStatus.OutcomeName(reader.GetByte(6));
            var next = reader.IsDBNull(9)
                ? null
                : AgentJobStatus.Timestamp(reader.GetInt32(9), reader.IsDBNull(10) ? 0 : reader.GetInt32(10));

            Dispatcher.UIThread.Post(() =>
            {
                _name.Text = _job;
                _enabled.IsChecked = enabled;
                _description.Text = description;
                _category.ItemsSource = categories;
                _category.SelectedItem = category;
                _owner.ItemsSource = owners;
                _owner.SelectedItem = owner;
            });

            _status.Set("created", created.ToString("yyyy-MM-dd HH:mm:ss"));
            _status.Set("modified", modified.ToString("yyyy-MM-dd HH:mm:ss"));
            _status.Set("lastRun", lastRun is null ? "Never run" : $"{lastRun}  ({outcome})");
            _status.Set("nextRun", next ?? "Not scheduled");
        }
        catch (Exception ex)
        {
            _status.Fail(ex);
            _report(ex.Message);
        }
    }

    private static async Task<List<string>> ListAsync(SqlConnection connection, string sql)
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
