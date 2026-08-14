using Microsoft.Data.SqlClient;

namespace DataTray.Providers.MsSql;

/// <summary>
/// The reads behind the SSIS step editor. All of it is SSISDB and msdb; nothing here writes.
/// </summary>
/// <remarks>
/// Every call assumes the catalog exists. It always does where this editor is reachable: the Steps page takes
/// its subsystem list from <c>msdb.dbo.syssubsystems</c>, which does not offer SSIS on a server that cannot
/// run it — so the SSIS editor never opens on, say, SQL Server on Linux.
/// </remarks>
internal static class SsisCatalog
{
    /// <summary>Every package in the catalog, in one read — a catalog is a handful of folders, not a table.</summary>
    public static async Task<List<SsisPackageRef>> PackagesAsync(SqlConnection connection)
    {
        var packages = new List<SsisPackageRef>();
        await using var command = new SqlCommand(
            """
            SELECT f.name, p.name, k.name
            FROM SSISDB.catalog.folders f
            JOIN SSISDB.catalog.projects p ON p.folder_id = f.folder_id
            JOIN SSISDB.catalog.packages k ON k.project_id = p.project_id
            ORDER BY f.name, p.name, k.name
            """, connection);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            packages.Add(new SsisPackageRef(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return packages;
    }

    /// <summary>
    /// The environment references configured on a project. A reference is relative when it carries no folder
    /// of its own, meaning the project's — which is why the project's folder is the fallback here.
    /// </summary>
    public static async Task<List<SsisEnvironmentRef>> EnvironmentsAsync(
        SqlConnection connection, string folder, string project)
    {
        var environments = new List<SsisEnvironmentRef>();
        await using var command = new SqlCommand(
            """
            SELECT r.reference_id, r.environment_name, ISNULL(r.environment_folder_name, f.name)
            FROM SSISDB.catalog.environment_references r
            JOIN SSISDB.catalog.projects p ON p.project_id = r.project_id
            JOIN SSISDB.catalog.folders f ON f.folder_id = p.folder_id
            WHERE f.name = @folder AND p.name = @project
            ORDER BY r.environment_name
            """, connection);
        command.Parameters.AddWithValue("folder", folder);
        command.Parameters.AddWithValue("project", project);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            environments.Add(new SsisEnvironmentRef(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return environments;
    }

    /// <summary>
    /// The connection managers a project exposes, so the overrides table can list them instead of asking for
    /// a name. In the catalog a connection manager is a parameter called <c>CM.&lt;name&gt;.ConnectionString</c>,
    /// held at project or package level — both count, hence the DISTINCT.
    /// </summary>
    public static async Task<List<string>> ConnectionManagersAsync(
        SqlConnection connection, string folder, string project)
    {
        var managers = new List<string>();
        await using var command = new SqlCommand(
            """
            SELECT DISTINCT op.parameter_name
            FROM SSISDB.catalog.object_parameters op
            JOIN SSISDB.catalog.projects p ON p.project_id = op.project_id
            JOIN SSISDB.catalog.folders f ON f.folder_id = p.folder_id
            WHERE f.name = @folder AND p.name = @project
              AND op.parameter_name LIKE 'CM.%.ConnectionString'
            ORDER BY op.parameter_name
            """, connection);
        command.Parameters.AddWithValue("folder", folder);
        command.Parameters.AddWithValue("project", project);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // CM.<name>.ConnectionString → <name>
            var parameter = reader.GetString(0);
            managers.Add(parameter["CM.".Length..^".ConnectionString".Length]);
        }

        return managers;
    }

    /// <summary>
    /// Enabled proxies by subsystem. A proxy is granted per subsystem in <c>sysproxysubsystem</c>, and offering
    /// one that lacks the grant produces error 14262 on save — or a step that cannot start. Read once and
    /// filtered in the page, so switching a step's type costs no round trip.
    /// </summary>
    public static async Task<Dictionary<string, List<string>>> ProxiesBySubsystemAsync(SqlConnection connection)
    {
        var proxies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(
            """
            SELECT s.subsystem, p.name
            FROM msdb.dbo.sysproxies p
            JOIN msdb.dbo.sysproxysubsystem ps ON ps.proxy_id = p.proxy_id
            JOIN msdb.dbo.syssubsystems s ON s.subsystem_id = ps.subsystem_id
            WHERE p.enabled = 1
            ORDER BY s.subsystem, p.name
            """, connection);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var subsystem = reader.GetString(0);
            if (!proxies.TryGetValue(subsystem, out var names))
            {
                names = [];
                proxies[subsystem] = names;
            }

            names.Add(reader.GetString(1));
        }

        return proxies;
    }
}
