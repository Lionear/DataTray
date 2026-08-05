using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DataTray.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace DataTray.Providers.MsSql;

/// <summary>
/// SSMS' "Job Properties" for a SQL Server Agent job (SE-235). Same shape as
/// <see cref="DatabasePropertiesView"/> — page rail on the left, detail on the right, built in code, each
/// page loading lazily the first time it is shown — but this one writes as well as reads: complete job
/// management is the point, not a viewer.
/// </summary>
/// <remarks>
/// SSMS' rail is General / Steps / Schedules / Alerts / Notifications / Targets, with history in a separate
/// Log File Viewer window; here History is the last page instead, so the dialog you open to ask "why did
/// this fail" answers it without a second window.
///
/// <see cref="NodeInfoContext"/> is documented as read-only but hands over the provider, so the write path
/// goes through the same <c>ExecuteDdlAsync</c> the Agent job tools use. No host API bump needed.
/// </remarks>
public sealed class AgentJobPropertiesView : UserControl
{
    private static readonly string[] Pages = ["General", "Steps", "Schedules", "Alerts", "Notifications", "Targets", "History"];

    private readonly NodeInfoContext _context;
    private readonly string _job;
    private readonly ContentControl _host = new();
    private readonly Control?[] _built = new Control?[Pages.Length];

    public AgentJobPropertiesView(NodeInfoContext context)
    {
        _context = context;
        _job = context.Node.Name;

        var rail = new ListBox
        {
            Width = 185,
            ItemsSource = Pages,
            SelectedIndex = 0,
            Background = Brushes.Transparent
        };
        rail.SelectionChanged += (_, _) => ShowPage(rail.SelectedIndex);

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(rail, 0);
        Grid.SetColumn(_host, 1);
        layout.Children.Add(rail);
        layout.Children.Add(_host);
        Content = layout;

        ShowPage(0);
    }

    private void ShowPage(int index)
    {
        if (index < 0)
        {
            return;
        }

        _built[index] ??= index switch
        {
            0 => BuildGeneral(),
            1 => new AgentJobStepsPage(_context).Control,
            2 => new AgentJobSchedulesPage(_context).Control,
            3 => new AgentJobAlertsPage(_context).Control,
            4 => new AgentJobNotificationsPage(_context).Control,
            5 => new AgentJobTargetsPage(_context).Control,
            6 => BuildHistory(),
            _ => new TextBlock()
        };

        _host.Content = _built[index];
    }

    // ── General ──────────────────────────────────────────────────────────────────────────────────────

    private Control BuildGeneral()
    {
        var enabled = new CheckBox { Content = "Enabled" };
        var description = new TextBox { AcceptsReturn = true, Height = 66, TextWrapping = TextWrapping.Wrap };
        var category = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var owner = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };

        var facts = new PropPage();
        facts.Row("Created", "created");
        facts.Row("Last modified", "modified");
        facts.Row("Last run", "lastRun");
        facts.Row("Next run", "nextRun");

        var status = new TextBlock { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        var save = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Right };

        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            status.Text = "Saving…";
            try
            {
                await SaveGeneralAsync(enabled.IsChecked == true, description.Text, category.SelectedItem as string,
                    owner.SelectedItem as string);
                status.Text = "Saved.";
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
            finally
            {
                save.IsEnabled = true;
            }
        };

        var form = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = _job, FontWeight = FontWeight.SemiBold },
                enabled,
                Labelled("Description", description),
                Labelled("Category", category),
                Labelled("Owner", owner),
                facts.Stack,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { status, save }
                }
            }
        };

        _ = LoadGeneralAsync(enabled, description, category, owner, facts, status);
        return new ScrollViewer { Content = form, Padding = new Thickness(12) };
    }

    private static Control Labelled(string label, Control editor) => new StackPanel
    {
        Spacing = 2,
        Children = { new TextBlock { Text = label, Opacity = 0.65 }, editor }
    };

    private async Task LoadGeneralAsync(
        CheckBox enabled, TextBox description, ComboBox category, ComboBox owner, PropPage facts, TextBlock status)
    {
        try
        {
            await using var connection = await OpenAsync();

            // The pickers first, so the value the job carries is already in the list when it is selected.
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
                Dispatcher.UIThread.Post(() => status.Text = "This job no longer exists.");
                return;
            }

            var isEnabled = reader.GetByte(0) == 1;
            var desc = reader.GetString(1);
            var cat = reader.GetString(2);
            var own = reader.GetString(3);
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
                enabled.IsChecked = isEnabled;
                description.Text = desc;
                category.ItemsSource = categories;
                category.SelectedItem = cat;
                owner.ItemsSource = owners;
                owner.SelectedItem = own;
            });

            facts.Set("created", created.ToString("yyyy-MM-dd HH:mm:ss"));
            facts.Set("modified", modified.ToString("yyyy-MM-dd HH:mm:ss"));
            facts.Set("lastRun", lastRun is null ? "Never run" : $"{lastRun} ({outcome})");
            facts.Set("nextRun", next ?? "Not scheduled");
        }
        catch (Exception ex)
        {
            facts.Fail(ex);
            Dispatcher.UIThread.Post(() => status.Text = ex.Message);
        }
    }

    private async Task SaveGeneralAsync(bool enabled, string? description, string? category, string? owner)
    {
        // sp_update_job ignores a parameter that is not passed, so only the four this page owns are sent.
        var sql = $"EXEC msdb.dbo.sp_update_job @job_name = N'{Escape(_job)}', @enabled = {(enabled ? 1 : 0)}"
                  + $", @description = N'{Escape(description ?? string.Empty)}'"
                  + (string.IsNullOrEmpty(category) ? "" : $", @category_name = N'{Escape(category)}'")
                  + (string.IsNullOrEmpty(owner) ? "" : $", @owner_login_name = N'{Escape(owner)}'");

        await _context.Provider.ExecuteDdlAsync(_context.Profile, sql, CancellationToken.None);
    }

    // ── History ──────────────────────────────────────────────────────────────────────────────────────

    private Control BuildHistory()
    {
        // Everything Agent kept, not an invented cap: SSMS does not limit this either, and Agent's own
        // retention (1000 rows, 100 per job by default) already bounds it.
        var table = new Table(
            ["Run", "Step", "Outcome", "Duration", "Message"],
            // Sized to what is left of a default-width dialog beside the rail, so the message —
            // the whole reason to open this page — is readable without scrolling sideways first.
            [140, 170, 85, 70, 265]);

        _ = LoadHistoryAsync(table);
        return new ScrollViewer { Content = table.Control, Padding = new Thickness(12) };
    }

    private async Task LoadHistoryAsync(Table table)
    {
        try
        {
            await using var connection = await OpenAsync();
            await using var command = new SqlCommand(
                """
                SELECT h.step_id, h.step_name, h.run_status, h.run_date, h.run_time, h.run_duration, h.message
                FROM msdb.dbo.sysjobhistory h
                JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id
                WHERE j.name = @name
                ORDER BY h.instance_id DESC
                """, connection);
            command.Parameters.AddWithValue("name", _job);

            var rows = new List<string[]>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var stepId = reader.GetInt32(0);
                rows.Add([
                    AgentJobStatus.Timestamp(reader.GetInt32(3), reader.GetInt32(4)) ?? "—",
                    stepId == 0 ? "(job outcome)" : $"{stepId} — {reader.GetString(1)}",
                    AgentJobStatus.OutcomeName(reader.GetInt32(2)),
                    AgentJobStatus.Duration(reader.GetInt32(5)),
                    reader.IsDBNull(6) ? "" : reader.GetString(6)
                ]);
            }

            table.Fill(rows);
        }
        catch (Exception ex)
        {
            table.Fail(ex);
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────────

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_context.Profile.ConnectionString);
        await connection.OpenAsync();
        return connection;
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
