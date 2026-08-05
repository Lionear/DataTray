using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DataTray.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace DataTray.Providers.MsSql;

/// <summary>
/// The History page of <see cref="AgentJobPropertiesView"/>: one row per run, opening to the step rows and
/// the message Agent recorded.
/// </summary>
/// <remarks>
/// Nested rather than flat, because a job with two steps writes three rows per run and a flat list buries the
/// run you are looking for under the detail of the ones you are not. The first row starts open, since a
/// dialog opened on this page was almost certainly opened to read the latest failure.
///
/// Everything Agent retained is shown: SSMS does not cap this either, and Agent's own retention (1000 rows,
/// 100 per job by default) already bounds it.
/// </remarks>
internal sealed class AgentJobHistoryPage : IJobPage
{
    private readonly NodeInfoContext _context;
    private readonly string _job;
    private readonly Action<string> _report;
    private readonly StackPanel _runs = new() { Spacing = 2 };

    public AgentJobHistoryPage(NodeInfoContext context, Action<string> report)
    {
        _context = context;
        _job = context.Node.Name;
        _report = report;

        Control = new ScrollViewer
        {
            Padding = new Thickness(4, 0, 14, 12),
            Content = new StackPanel { Spacing = 8, Children = { FormBits.Section("Runs"), _runs } }
        };

        _ = LoadAsync();
    }

    public Control Control { get; }

    /// <summary>A log has nothing to save.</summary>
    public string? ActionLabel => null;

    public Task SaveAsync() => Task.CompletedTask;

    private sealed record Step(int Id, string Name, string Outcome, string Message);

    private sealed record Run(string When, string Outcome, string Duration, List<Step> Steps);

    private async Task LoadAsync()
    {
        try
        {
            await using var connection = new SqlConnection(_context.Profile.ConnectionString);
            await connection.OpenAsync();

            await using var command = new SqlCommand(
                """
                SELECT h.step_id, h.step_name, h.run_status, h.run_date, h.run_time, h.run_duration,
                       ISNULL(h.message, '')
                FROM msdb.dbo.sysjobhistory h
                JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id
                WHERE j.name = @name
                ORDER BY h.instance_id DESC
                """, connection);
            command.Parameters.AddWithValue("name", _job);

            // Ordered newest first, a run's own outcome row (step_id 0) arrives before its step rows.
            var runs = new List<Run>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var stepId = reader.GetInt32(0);
                var outcome = AgentJobStatus.OutcomeName(reader.GetInt32(2));
                if (stepId == 0)
                {
                    runs.Add(new Run(
                        AgentJobStatus.Timestamp(reader.GetInt32(3), reader.GetInt32(4)) ?? "—",
                        outcome, AgentJobStatus.Duration(reader.GetInt32(5)), []));
                }
                else if (runs.Count > 0)
                {
                    runs[^1].Steps.Add(new Step(stepId, reader.GetString(1), outcome, reader.GetString(6)));
                }
            }

            Dispatcher.UIThread.Post(() => Render(runs));
        }
        catch (Exception ex)
        {
            _report(ex.Message);
        }
    }

    private void Render(List<Run> runs)
    {
        _runs.Children.Clear();
        if (runs.Count == 0)
        {
            _runs.Children.Add(new TextBlock { Text = "This job has not run yet.", Opacity = 0.7 });
            return;
        }

        for (var i = 0; i < runs.Count; i++)
        {
            _runs.Children.Add(RunEntry(runs[i], expanded: i == 0));
        }
    }

    private static Control RunEntry(Run run, bool expanded)
    {
        var caret = new TextBlock { Text = expanded ? "▾" : "▸", Opacity = 0.6, Width = 16 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,170,110,*") };
        Add(header, caret, 0);
        Add(header, new TextBlock { Text = run.When }, 1);
        Add(header, Coloured(run.Outcome), 2);
        Add(header, new TextBlock { Text = run.Duration, Opacity = 0.7 }, 3);

        var detail = new StackPanel { Spacing = 6, Margin = new Thickness(24, 4, 0, 8), IsVisible = expanded };
        foreach (var step in run.Steps)
        {
            detail.Children.Add(Coloured($"{step.Id} — {step.Name}   ({step.Outcome})", step.Outcome));
            detail.Children.Add(new TextBlock
            {
                Text = step.Message,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
                FontFamily = FontFamily.Parse("Consolas, Menlo, monospace"),
                FontSize = 11.5
            });
        }

        if (run.Steps.Count == 0)
        {
            detail.Children.Add(new TextBlock { Text = "No step detail retained.", Opacity = 0.6 });
        }

        var button = new Button
        {
            Content = header,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 3, 4, 3)
        };
        button.Click += (_, _) =>
        {
            detail.IsVisible = !detail.IsVisible;
            caret.Text = detail.IsVisible ? "▾" : "▸";
        };

        return new StackPanel { Children = { button, detail } };
    }

    /// <summary>A block tinted by outcome — but only when there is a tint, since a null Foreground in
    /// Avalonia means "no brush" rather than "the default one".</summary>
    private static TextBlock Coloured(string text, string? outcome = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (OutcomeBrush(outcome ?? text) is { } brush)
        {
            block.Foreground = brush;
        }

        return block;
    }

    private static void Add(Grid grid, Control control, int column)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private static IBrush? OutcomeBrush(string outcome) => outcome switch
    {
        "failed" => new SolidColorBrush(Color.FromRgb(0xD6, 0x45, 0x45)),
        "canceled" or "retry" => new SolidColorBrush(Color.FromRgb(0xE0, 0xA3, 0x3E)),
        "succeeded" => new SolidColorBrush(Color.FromRgb(0x5A, 0xA5, 0x76)),
        _ => null
    };
}
