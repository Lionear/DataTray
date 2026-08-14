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
internal sealed class AgentJobNotificationsPage : IJobPage
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

    private readonly Action<string> _report;

    public AgentJobNotificationsPage(NodeInfoContext context, Action<string> report)
    {
        _context = context;
        _job = context.Node.Name;
        _report = report;
        Control = Build();
        _ = LoadAsync();
    }

    public Control Control { get; }

    public string ActionLabel => "Save";

    private static ComboBox Level() => new()
    {
        ItemsSource = Levels.Select(l => l.Label).ToList(),
        SelectedIndex = 0
    };

    private static ComboBox Operator() => new();

    private Control Build()
    {
        // A table rather than four stacked pairs of dropdowns: channel, when, operator are three columns,
        // and the operator only means anything once the level is above Never.
        foreach (var (level, target) in Channels())
        {
            level.SelectionChanged += (_, _) => SyncOperators();
            if (target is not null)
            {
                target.IsEnabled = false;
            }
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("200,230,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto")
        };

        Header(grid, "Channel", 0);
        Header(grid, "When", 1);
        Header(grid, "Operator", 2);

        Channel(grid, 1, "E-mail", _emailLevel, _emailOperator);
        Channel(grid, 2, "Net send", _netSendLevel, _netSendOperator);
        Channel(grid, 3, "Pager", _pageLevel, _pageOperator);
        Channel(grid, 4, "Windows application event log", _eventLogLevel, null);

        return FormBits.Page(FormBits.Section("When this job completes, notify"), grid);
    }

    private (ComboBox Level, ComboBox? Target)[] Channels() =>
    [
        (_emailLevel, _emailOperator), (_netSendLevel, _netSendOperator),
        (_pageLevel, _pageOperator), (_eventLogLevel, null)
    ];

    private static void Header(Grid grid, string text, int column)
    {
        var block = new TextBlock
        {
            Text = text, FontWeight = FontWeight.SemiBold, Opacity = 0.75,
            Margin = new Thickness(0, 0, 12, 6)
        };
        Grid.SetColumn(block, column);
        Grid.SetRow(block, 0);
        grid.Children.Add(block);
    }

    private static void Channel(Grid grid, int row, string label, ComboBox level, ComboBox? target)
    {
        var name = new TextBlock
        {
            Text = label, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 12, 3)
        };
        Grid.SetColumn(name, 0);
        Grid.SetRow(name, row);
        grid.Children.Add(name);

        level.Margin = new Thickness(0, 3, 12, 3);
        Grid.SetColumn(level, 1);
        Grid.SetRow(level, row);
        grid.Children.Add(level);

        if (target is null)
        {
            return;
        }

        target.Margin = new Thickness(0, 3, 0, 3);
        Grid.SetColumn(target, 2);
        Grid.SetRow(target, row);
        grid.Children.Add(target);
    }

    // An operator on a channel set to Never is a value that will never be used; say so by greying it.
    private void SyncOperators()
    {
        foreach (var (level, target) in Channels())
        {
            if (target is not null)
            {
                target.IsEnabled = Pick(level) != 0;
            }
        }
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
                SyncOperators();
            });
        }
        catch (Exception ex)
        {
            _report(ex.Message);
        }
    }

    public Task SaveAsync()
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
