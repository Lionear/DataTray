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

    /// <summary>One row per instance (the LEFT JOINs keep it at one when there is no endpoint): identity,
    /// version, edition, whether Always On is on, the login's rights, the WSFC it is a node of, and its
    /// database mirroring endpoint — the facts the new-group pre-flight decides on.</summary>
    public const string Instance =
        """
        SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)),
               CAST(SERVERPROPERTY('ProductMajorVersion') AS int),
               CAST(SERVERPROPERTY('Collation') AS nvarchar(128)),
               CAST(SERVERPROPERTY('EngineEdition') AS int),
               CAST(ISNULL(SERVERPROPERTY('IsHadrEnabled'), 0) AS int),
               HAS_PERMS_BY_NAME(NULL, NULL, 'CONTROL SERVER'),
               (SELECT TOP 1 NULLIF(cluster_name, '') FROM sys.dm_hadr_cluster),
               e.name, t.port, e.state_desc, e.connection_auth_desc
        FROM (SELECT 1 AS one) x
        LEFT JOIN sys.database_mirroring_endpoints e ON 1 = 1
        LEFT JOIN sys.tcp_endpoints t ON t.endpoint_id = e.endpoint_id
        """;

    public const string DatabaseNames = "SELECT name FROM sys.databases";

    /// <summary>The primary's user databases with every property that decides whether one can join.</summary>
    public const string CandidateDatabases =
        """
        SELECT d.name, d.state_desc, d.recovery_model_desc,
               CASE WHEN d.group_database_id IS NULL THEN 0 ELSE 1 END,
               d.is_auto_close_on, d.user_access_desc, d.is_read_only,
               CASE WHEN EXISTS (SELECT 1 FROM msdb.dbo.backupset b WHERE b.database_name = d.name AND b.type = 'D') THEN 1 ELSE 0 END,
               CASE WHEN m.mirroring_guid IS NULL THEN 0 ELSE 1 END
        FROM sys.databases d
        LEFT JOIN sys.database_mirroring m ON m.database_id = d.database_id
        WHERE d.database_id > 4
        ORDER BY d.name
        """;

    public static InstanceFacts ParseInstance(object?[] row, IReadOnlyList<object?[]> databaseNames) => new(
        Req(row[0]),
        Convert.ToInt32(row[1]),
        Req(row[2]),
        Convert.ToInt32(row[3]),
        Convert.ToInt32(row[4]) == 1,
        Convert.ToInt32(row[5] ?? 0) == 1,
        Str(row[6]),
        Str(row[7]) is { } endpoint
            ? new MirroringEndpoint(endpoint, row[8] is null or DBNull ? 0 : Convert.ToInt32(row[8]), Str(row[9])?.ToUpperInvariant() ?? "", Str(row[10]) ?? "")
            : null,
        databaseNames.Select(r => Req(r[0])).ToList());

    public static DatabaseFacts ParseDatabase(object?[] r) => new(
        Req(r[0]), Req(r[1]), Req(r[2]), Convert.ToInt32(r[3]) == 1, Bool(r[4]) == true, Req(r[5]), Bool(r[6]) == true, Convert.ToInt32(r[7]) == 1, Convert.ToInt32(r[8]) == 1);

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
