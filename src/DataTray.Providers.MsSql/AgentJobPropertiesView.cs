using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DataTray.Sdk.Ui;

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
/// The primary action lives in a footer bar owned by this view rather than at the end of each page's form,
/// where on the longer pages it sat below the fold. Its label comes from the page — "Save step" says
/// something "Save" does not when the page holds a list and an editor for one row of it.
///
/// <see cref="NodeInfoContext"/> is documented as read-only but hands over the provider, so the write path
/// goes through the same <c>ExecuteDdlAsync</c> the Agent job tools use. No host API bump needed.
/// </remarks>
public sealed class AgentJobPropertiesView : UserControl
{
    private static readonly string[] Pages =
        ["General", "Steps", "Schedules", "Alerts", "Notifications", "Targets", "History"];

    private readonly NodeInfoContext _context;
    private readonly ContentControl _host = new();
    private readonly IJobPage?[] _built = new IJobPage?[Pages.Length];

    private readonly TextBlock _status = new()
    {
        Opacity = 0.75,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly Button _action = new() { MinWidth = 96 };

    private IJobPage? _current;

    public AgentJobPropertiesView(NodeInfoContext context)
    {
        _context = context;

        var rail = new ListBox
        {
            Width = 170,
            ItemsSource = Pages,
            SelectedIndex = 0,
            Background = Brushes.Transparent
        };
        rail.SelectionChanged += (_, _) => ShowPage(rail.SelectedIndex);

        _action.Click += async (_, _) =>
        {
            if (_current is null)
            {
                return;
            }

            _action.IsEnabled = false;
            try
            {
                await _current.SaveAsync();
            }
            catch (Exception ex)
            {
                Report(ex.Message);
            }
            finally
            {
                _action.IsEnabled = true;
            }
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(rail, 0);
        Grid.SetColumn(_host, 1);
        body.Children.Add(rail);
        body.Children.Add(_host);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 10, 0, 0)
        };
        Grid.SetColumn(_status, 0);
        Grid.SetColumn(_action, 1);
        footer.Children.Add(_status);
        footer.Children.Add(_action);

        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(body, 0);
        Grid.SetRow(footer, 1);
        layout.Children.Add(body);
        layout.Children.Add(footer);
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
            0 => new AgentJobGeneralPage(_context, Report),
            1 => new AgentJobStepsPage(_context, Report),
            2 => new AgentJobSchedulesPage(_context, Report),
            3 => new AgentJobAlertsPage(_context, Report),
            4 => new AgentJobNotificationsPage(_context, Report),
            5 => new AgentJobTargetsPage(_context, Report),
            _ => new AgentJobHistoryPage(_context, Report)
        };

        _current = _built[index];
        _host.Content = _current!.Control;
        _status.Text = "";
        _action.Content = _current.ActionLabel ?? "";
        _action.IsVisible = _current.ActionLabel is not null;
    }

    // Every page reports through here, so the dialog has one status line rather than one per page.
    private void Report(string message) => Dispatcher.UIThread.Post(() => _status.Text = message);
}
