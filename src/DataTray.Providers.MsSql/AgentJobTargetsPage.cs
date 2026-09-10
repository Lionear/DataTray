using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DataTray.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace DataTray.Providers.MsSql;

/// <summary>
/// The Targets page of <see cref="AgentJobPropertiesView"/> (SE-235): which servers run this job.
/// </summary>
/// <remarks>
/// On a standalone server this is one checkbox, and the honest thing is to say so rather than dress it up:
/// a job with no target server is configured but will never run, which is a real and easily-missed state —
/// <c>sp_add_job</c> without <c>sp_add_jobserver</c> leaves exactly that. Multi-server targets only exist on
/// a master server, so the list appears only when <c>systargetservers</c> has rows.
/// </remarks>
internal sealed class AgentJobTargetsPage : IJobPage
{
    private const string LocalServer = "(local)";

    private readonly NodeInfoContext _context;
    private readonly string _job;

    private readonly CheckBox _local = new() { Content = "Target the local server" };
    private readonly StackPanel _targets = new() { Spacing = 4 };
    private readonly TextBlock _targetsHeader = new() { Opacity = 0.65, IsVisible = false };
    private readonly Action<string> _report;

    private readonly List<CheckBox> _targetBoxes = [];
    private HashSet<string> _attached = [];
    private string _localName = "";

    public AgentJobTargetsPage(NodeInfoContext context, Action<string> report)
    {
        _context = context;
        _job = context.Node.Name;
        _report = report;
        Control = Build();
        _ = ReloadAsync();
    }

    public Control Control { get; }

    public string ActionLabel => "Save";

    private Control Build() => FormBits.Page(
        FormBits.Section("Servers that run this job"),
        _local,
        _targetsHeader,
        _targets,
        new TextBlock
        {
            Text = "Target servers appear here as well on a master server. A job with no server checked is "
                   + "configured but will never fire.",
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap
        });

    private async Task ReloadAsync()
    {
        try
        {
            await using var connection = await OpenAsync();

            // server_id 0 is the local server; anything else is a target server enlisted with a master.
            var attached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var command = new SqlCommand(
                             """
                             SELECT CASE WHEN js.server_id = 0 THEN '(local)' ELSE ISNULL(ts.server_name, '') END
                             FROM msdb.dbo.sysjobservers js
                             JOIN msdb.dbo.sysjobs j ON j.job_id = js.job_id
                             LEFT JOIN msdb.dbo.systargetservers ts ON ts.server_id = js.server_id
                             WHERE j.name = @name
                             """, connection))
            {
                command.Parameters.AddWithValue("name", _job);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    attached.Add(reader.GetString(0));
                }
            }

            // sp_delete_jobserver has no default for @server_name the way sp_add_jobserver does, so the
            // local server's real name has to be known before anything can be detached from it.
            await using (var command = new SqlCommand(
                             "SELECT CAST(SERVERPROPERTY('ServerName') AS sysname)", connection))
            {
                _localName = await command.ExecuteScalarAsync() as string ?? "";
            }

            var known = new List<string>();
            await using (var command = new SqlCommand(
                             "SELECT server_name FROM msdb.dbo.systargetservers ORDER BY server_name", connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    known.Add(reader.GetString(0));
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                _attached = attached;
                _local.IsChecked = attached.Contains(LocalServer);

                _targetBoxes.Clear();
                _targets.Children.Clear();
                _targetsHeader.IsVisible = known.Count > 0;
                _targetsHeader.Text = "Target servers";
                foreach (var server in known)
                {
                    var box = new CheckBox { Content = server, IsChecked = attached.Contains(server) };
                    _targetBoxes.Add(box);
                    _targets.Children.Add(box);
                }

                if (attached.Count == 0)
                {
                    _report("No server runs this job — it is configured but will never fire.");
                }
            });
        }
        catch (Exception ex)
        {
            _report(ex.Message);
        }
    }

    public async Task SaveAsync()
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_local.IsChecked == true)
        {
            wanted.Add(LocalServer);
        }

        foreach (var box in _targetBoxes.Where(b => b.IsChecked == true))
        {
            wanted.Add((string)box.Content!);
        }

        // Only the difference is written, so saving an unchanged page is a no-op rather than a churn of
        // delete-then-add that would lose the job's per-server history.
        var statements = new List<string>();
        foreach (var server in wanted.Except(_attached, StringComparer.OrdinalIgnoreCase))
        {
            statements.Add($"EXEC msdb.dbo.sp_add_jobserver @job_name = N'{Escape(_job)}'{ServerArg(server)}");
        }

        foreach (var server in _attached.Except(wanted, StringComparer.OrdinalIgnoreCase))
        {
            statements.Add($"EXEC msdb.dbo.sp_delete_jobserver @job_name = N'{Escape(_job)}'{ServerArg(server)}");
        }

        if (statements.Count == 0)
        {
            _report("Nothing to change.");
            return;
        }

        await _context.Provider.ExecuteDdlAsync(_context.Profile, string.Join(";\n", statements),
            CancellationToken.None);
        _report("Targets saved.");
        await ReloadAsync();
    }

    // Always named: only sp_add_jobserver defaults to the local server, and sp_delete_jobserver refuses the
    // call outright without it.
    private string ServerArg(string server) =>
        $", @server_name = N'{Escape(server == LocalServer ? _localName : server)}'";

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_context.Profile.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
