using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DataTray.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace DataTray.Providers.MsSql;

/// <summary>
/// The Notifications page of <see cref="AgentJobPropertiesView"/> (SE-235): who Agent tells, and when, once
/// a job finishes. Four channels, each a level plus (except the event log) an operator.
/// </summary>
/// <remarks>
/// E-mail and pager only reach anyone if Database Mail and an operator are configured, and net send needs the
/// Messenger service — none of which this page can arrange. It writes the job's intent; whether the message
/// arrives is Agent's side of the deal.
/// </remarks>
internal sealed class AgentJobNotificationsPage
{
    private static readonly (int Value, string Label)[] Levels =
    [
        (0, "Never"),
        (1, "When the job succeeds"),
        (2, "When the job fails"),
        (3, "When the job completes")
    ];

    private const string NoOperator = "(none)";

    private readonly NodeInfoContext _context;
    private readonly string _job;

    private readonly ComboBox _emailLevel = Level();
    private readonly ComboBox _emailOperator = Operator();
    private readonly ComboBox _netSendLevel = Level();
    private readonly ComboBox _netSendOperator = Operator();
    private readonly ComboBox _pageLevel = Level();
    private readonly ComboBox _pageOperator = Operator();
    private readonly ComboBox _eventLogLevel = Level();

    private readonly TextBlock _status = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };

    public AgentJobNotificationsPage(NodeInfoContext context)
    {
        _context = context;
        _job = context.Node.Name;
        Control = Build();
        _ = LoadAsync();
    }

    public Control Control { get; }

    private static ComboBox Level() => new()
    {
        Width = 220,
        ItemsSource = Levels.Select(l => l.Label).ToList(),
        SelectedIndex = 0
    };

    private static ComboBox Operator() => new() { Width = 220 };

    private Control Build()
    {
        var save = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                await SaveAsync();
                _status.Text = "Saved.";
            }
            catch (Exception ex)
            {
                _status.Text = ex.Message;
            }
            finally
            {
                save.IsEnabled = true;
            }
        };

        var page = FormBits.Page(
            FormBits.Section("When this job completes, notify"),
            Channel("E-mail", _emailLevel, _emailOperator),
            Channel("Net send", _netSendLevel, _netSendOperator),
            Channel("Pager", _pageLevel, _pageOperator),
            Channel("Windows application event log", _eventLogLevel, null),
            new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8, Children = { _status, save }
            });

        return page;
    }

    private static Control Channel(string label, ComboBox level, ComboBox? target)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { level } };
        if (target is not null)
        {
            line.Children.Add(target);
        }

        return FormBits.Labelled(label, line);
    }

    private async Task LoadAsync()
    {
        try
        {
            await using var connection = await OpenAsync();

            var operators = new List<string> { NoOperator };
            await using (var list = new SqlCommand("SELECT name FROM msdb.dbo.sysoperators ORDER BY name", connection))
            await using (var reader = await list.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    operators.Add(reader.GetString(0));
                }
            }

            await using var command = new SqlCommand(
                """
                SELECT j.notify_level_email, ISNULL(e.name, ''), j.notify_level_netsend, ISNULL(n.name, ''),
                       j.notify_level_page, ISNULL(p.name, ''), j.notify_level_eventlog
                FROM msdb.dbo.sysjobs j
                LEFT JOIN msdb.dbo.sysoperators e ON e.id = j.notify_email_operator_id
                LEFT JOIN msdb.dbo.sysoperators n ON n.id = j.notify_netsend_operator_id
                LEFT JOIN msdb.dbo.sysoperators p ON p.id = j.notify_page_operator_id
                WHERE j.name = @name
                """, connection);
            command.Parameters.AddWithValue("name", _job);

            await using var row = await command.ExecuteReaderAsync();
            if (!await row.ReadAsync())
            {
                return;
            }

            var values = new[]
            {
                (row.GetInt32(0), row.GetString(1)), (row.GetInt32(2), row.GetString(3)),
                (row.GetInt32(4), row.GetString(5))
            };
            var eventLog = row.GetInt32(6);

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var box in new[] { _emailOperator, _netSendOperator, _pageOperator })
                {
                    box.ItemsSource = operators.ToList();
                }

                var channels = new[]
                {
                    (_emailLevel, _emailOperator), (_netSendLevel, _netSendOperator), (_pageLevel, _pageOperator)
                };
                for (var i = 0; i < channels.Length; i++)
                {
                    channels[i].Item1.SelectedIndex = IndexOf(values[i].Item1);
                    channels[i].Item2.SelectedItem = string.IsNullOrEmpty(values[i].Item2)
                        ? NoOperator
                        : values[i].Item2;
                }

                _eventLogLevel.SelectedIndex = IndexOf(eventLog);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => _status.Text = ex.Message);
        }
    }

    private Task SaveAsync()
    {
        // An operator name is only meaningful with a level above Never; sending one for a disabled channel
        // makes Agent reject the whole call.
        var sql = $"EXEC msdb.dbo.sp_update_job @job_name = N'{Escape(_job)}'"
                  + $", @notify_level_email = {Pick(_emailLevel)}"
                  + Named("@notify_email_operator_name", _emailLevel, _emailOperator)
                  + $", @notify_level_netsend = {Pick(_netSendLevel)}"
                  + Named("@notify_netsend_operator_name", _netSendLevel, _netSendOperator)
                  + $", @notify_level_page = {Pick(_pageLevel)}"
                  + Named("@notify_page_operator_name", _pageLevel, _pageOperator)
                  + $", @notify_level_eventlog = {Pick(_eventLogLevel)}";

        return _context.Provider.ExecuteDdlAsync(_context.Profile, sql, CancellationToken.None);
    }

    private static string Named(string parameter, ComboBox level, ComboBox target) =>
        Pick(level) == 0 || target.SelectedItem is not string name || name == NoOperator
            ? ""
            : $", {parameter} = N'{Escape(name)}'";

    private static int Pick(ComboBox box) => Levels[Math.Clamp(box.SelectedIndex, 0, Levels.Length - 1)].Value;

    private static int IndexOf(int level) => Math.Max(0, Array.FindIndex(Levels, l => l.Value == level));

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_context.Profile.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
