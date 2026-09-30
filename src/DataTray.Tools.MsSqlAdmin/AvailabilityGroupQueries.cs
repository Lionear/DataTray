using static DataTray.Tools.MsSqlAdmin.AvailabilityGroupFailover;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// The four reads the failover planner is built from, and the parsing of their rows into an
/// <see cref="AgTopology"/>. Shaped after the SE-284 dashboard's queries (<c>AvailabilityGroupDashboardView</c>
/// in the MSSQL provider) and carrying the same lessons: <c>_desc</c> values compared case-insensitively,
/// small integer DMV columns read through <see cref="Convert"/>, and database names taken from
/// <c>dm_hadr_database_replica_cluster_states</c> because <c>database_id</c> is instance-local. They cannot be
/// literally shared: that view talks to <c>SqlConnection</c> with parameters, and this plugin reaches SQL
/// Server only through the host's provider — hence the group name as an escaped literal here.
/// </summary>
/// <remarks>
/// Every query tolerates being run on a secondary, which only sees its own rows in the replica/database
/// state DMVs: the joins to them are LEFT joins, and whatever is missing stays null in the topology rather
/// than being guessed.
/// </remarks>
internal static class AvailabilityGroupQueries
{
    public static string Group(string group) =>
        $"""
        SELECT ag.cluster_type_desc, ag.required_synchronized_secondaries_to_commit, ags.primary_replica
        FROM sys.availability_groups ag
        LEFT JOIN sys.dm_hadr_availability_group_states ags ON ags.group_id = ag.group_id
        WHERE ag.name = {Lit(group)}
        """;

    public static string Replicas(string group) =>
        $"""
        SELECT ar.replica_server_name, ar.availability_mode_desc, ars.connected_state_desc, ars.is_local
        FROM sys.availability_replicas ar
        JOIN sys.availability_groups ag ON ag.group_id = ar.group_id
        LEFT JOIN sys.dm_hadr_availability_replica_states ars ON ars.replica_id = ar.replica_id
        WHERE ag.name = {Lit(group)}
        ORDER BY ar.replica_server_name
        """;

    public static string Databases(string group) =>
        $"""
        SELECT dcs.database_name, ar.replica_server_name, drs.synchronization_state_desc,
               dcs.is_failover_ready, drs.last_commit_time
        FROM sys.dm_hadr_database_replica_cluster_states dcs
        JOIN sys.availability_replicas ar ON ar.replica_id = dcs.replica_id
        JOIN sys.availability_groups ag ON ag.group_id = ar.group_id
        LEFT JOIN sys.dm_hadr_database_replica_states drs
            ON drs.replica_id = dcs.replica_id AND drs.group_database_id = dcs.group_database_id
        WHERE ag.name = {Lit(group)}
        ORDER BY dcs.database_name, ar.replica_server_name
        """;

    public static string Listener(string group) =>
        $"""
        SELECT l.dns_name, l.port, ip.ip_address, ip.ip_subnet_mask, ip.is_dhcp
        FROM sys.availability_group_listeners l
        JOIN sys.availability_groups ag ON ag.group_id = l.group_id
        LEFT JOIN sys.availability_group_listener_ip_addresses ip ON ip.listener_id = l.listener_id
        WHERE ag.name = {Lit(group)}
        """;

    /// <summary>The instance's own name as a replica name has to match it — the check that a saved
    /// connection really points at the replica it was picked for.</summary>
    public const string ServerName = "SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256))";

    /// <summary>The replica name the launch connection is, or null when it hosts none of the group's replicas.</summary>
    public static string? LocalReplica(IReadOnlyList<object?[]> replicaRows) =>
        replicaRows.FirstOrDefault(r => Bool(r[3]) == true) is { } row ? Str(row[0]) : null;

    public static AgTopology Parse(
        string group,
        IReadOnlyList<object?[]> groupRows,
        IReadOnlyList<object?[]> replicaRows,
        IReadOnlyList<object?[]> databaseRows,
        IReadOnlyList<object?[]> listenerRows)
    {
        if (groupRows.Count == 0)
        {
            throw new InvalidOperationException($"Availability group {group} does not exist on this instance.");
        }

        var g = groupRows[0];
        var replicas = replicaRows
            .Select(r => new AgReplica(
                Req(r[0]),
                string.Equals(Str(r[1]), "SYNCHRONOUS_COMMIT", StringComparison.OrdinalIgnoreCase),
                Str(r[2]) is { } connected ? string.Equals(connected, "CONNECTED", StringComparison.OrdinalIgnoreCase) : null))
            .ToList();
        var databases = databaseRows
            .Select(r => new AgDatabase(Req(r[0]), Req(r[1]), Str(r[2])?.ToUpperInvariant(), Bool(r[3]) == true, r[4] as DateTime?))
            .ToList();

        AgListener? listener = null;
        if (listenerRows.Count > 0)
        {
            var first = listenerRows[0];
            var addresses = listenerRows
                .Where(r => r[2] is not null && Bool(r[4]) != true)
                .Select(r => (Req(r[2]), Str(r[3]) ?? string.Empty))
                .ToList();
            listener = new AgListener(Req(first[0]), Convert.ToInt32(first[1]), addresses);
        }

        return new AgTopology(group, Str(g[0]), Str(g[2]), g[1] is null ? 0 : Convert.ToInt32(g[1]),
            replicas, databases, listener);
    }

    // For columns the catalog declares NOT NULL (names); a null there means the query is wrong, not the data.
    private static string Req(object? value) =>
        Str(value) ?? throw new InvalidOperationException("An availability group catalog column that is never NULL came back NULL.");

    private static string? Str(object? value) => value is null or DBNull ? null : Convert.ToString(value);

    private static bool? Bool(object? value) => value is null or DBNull ? null : Convert.ToBoolean(value);
}
