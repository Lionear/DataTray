using static DataTray.Tools.MsSqlAdmin.AvailabilityGroupFailover;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>What one instance reports about itself — everything the pre-flight decides on.</summary>
/// <param name="EngineEdition"><c>SERVERPROPERTY('EngineEdition')</c>: 2 = Standard, 3 = Enterprise (and
/// Developer/Evaluation), 4 = Express, 5+ = the Azure flavours.</param>
/// <param name="WsfcCluster">The Windows failover cluster this node belongs to (<c>sys.dm_hadr_cluster</c>),
/// null when it belongs to none.</param>
/// <param name="Endpoint">The instance's DATABASE_MIRRORING endpoint, if it has one.</param>
internal sealed record InstanceFacts(
    string Name,
    int MajorVersion,
    string Collation,
    int EngineEdition,
    bool HadrEnabled,
    bool IsControlServer,
    string? WsfcCluster,
    MirroringEndpoint? Endpoint,
    IReadOnlyList<string> Databases);

internal sealed record MirroringEndpoint(string Name, int Port, string State, string Authentication);

/// <summary>A user database on the primary, with every property that decides whether it can join a group.</summary>
internal sealed record DatabaseFacts(
    string Name,
    string State,
    string RecoveryModel,
    bool InAvailabilityGroup,
    bool AutoClose,
    string UserAccess,
    bool ReadOnly,
    bool HasFullBackup,
    bool Mirrored = false);

internal enum CheckLevel
{
    Ok,
    Warning,
    Blocking
}

internal sealed record PreflightCheck(string Subject, string Message, CheckLevel Level);

/// <summary>
/// The wizard's gate: what blocks, what only warns, and — as a consequence — which choices the form offers
/// at all. A choice that cannot succeed on this topology is absent rather than disabled.
/// </summary>
internal sealed record Preflight(
    IReadOnlyList<PreflightCheck> Checks,
    IReadOnlyList<string> ClusterTypes,
    bool BasicOnly,
    bool CreateEndpoints,
    IReadOnlyList<(string Name, string? Why)> Databases)
{
    public bool CanProceed => Checks.All(c => c.Level != CheckLevel.Blocking) && ClusterTypes.Count > 0;
}

internal sealed record ReplicaChoice(string Name, string EndpointHost, bool Synchronous, bool AutomaticFailover);

internal sealed record NewGroupChoices(
    string Group,
    string ClusterType,
    IReadOnlyList<ReplicaChoice> Replicas,
    IReadOnlyList<string> Databases,
    string BackupPreference,
    int EndpointPort);

/// <summary>
/// Pre-flight and plan for creating an availability group across instances DataTray already has connections
/// to (SE-247). Pure, like <see cref="AvailabilityGroupFailover"/>: the facts are read elsewhere, and every
/// rule here is tested as data.
/// </summary>
/// <remarks>
/// Decisions that are the plan's shape, not a detail of it:
/// <list type="bullet">
/// <item><b>Endpoints authenticate by certificate</b>, always, when DataTray creates them. It works in a domain,
/// across domains and without one, and it is the only mode that works in all three — Windows authentication
/// fails between service accounts that do not trust each other, with <c>NT AUTHORITY\ANONYMOUS LOGON</c>. The
/// public certificate moves as <c>CERTENCODED()</c> → <c>CREATE CERTIFICATE … FROM BINARY</c>, so no file
/// is written on a server and no share is needed (the lab proved it; the cross-domain question the ticket
/// left open is answered by not moving a file at all).</item>
/// <item><b>Existing endpoints are reused as they are, or not at all.</b> When every replica already has one,
/// DataTray uses them and changes nothing about their authentication. When only some do, the wizard stops:
/// mixing an endpoint DataTray did not make with ones it did is how a join fails in ways nobody can see from here.</item>
/// <item><b>Automatic seeding only.</b> Manual seeding means moving backups between servers.</item>
/// <item><b>No listener, no read-only routing, no distributed or contained groups.</b> A listener on WSFC needs
/// cluster rights in Active Directory that DataTray cannot check; read-only routing is its own feature.</item>
/// </list>
/// </remarks>
internal static class NewAvailabilityGroupPlan
{
    public const string EndpointName = "Hadr_endpoint";

    public static Preflight Check(IReadOnlyList<InstanceFacts> instances, IReadOnlyList<DatabaseFacts> primaryDatabases)
    {
        var checks = new List<PreflightCheck>();
        if (instances.Count < 2)
        {
            checks.Add(new("Replicas", "An availability group needs at least two instances. Pick a saved connection to another SQL Server instance as a secondary.", CheckLevel.Blocking));
        }

        foreach (var dup in instances.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            checks.Add(new(dup.Key, "Two of the picked connections reach this same instance.", CheckLevel.Blocking));
        }

        foreach (var i in instances)
        {
            if (i.EngineEdition is not (2 or 3))
            {
                checks.Add(new(i.Name, "This edition has no availability groups (Enterprise, Developer and Standard do).", CheckLevel.Blocking));
            }

            if (i.MajorVersion < 13)
            {
                checks.Add(new(i.Name, "SQL Server 2016 or later is needed.", CheckLevel.Blocking));
            }

            if (!i.HadrEnabled)
            {
                checks.Add(new(i.Name, "Always On is not enabled. It cannot be switched on from a connection: on Windows, enable it in SQL Server Configuration Manager; on Linux, run 'mssql-conf set hadr.hadrenabled 1' — then restart the service.", CheckLevel.Blocking));
            }

            if (!i.IsControlServer)
            {
                checks.Add(new(i.Name, "This connection's login lacks CONTROL SERVER, which creating endpoints, certificates and logins needs.", CheckLevel.Blocking));
            }
        }

        // "Each server instance must be running the same version of SQL Server" and "the same SQL Server
        // collation" (Microsoft Learn, prerequisites for availability groups).
        if (instances.Select(i => i.MajorVersion).Distinct().Count() > 1)
        {
            checks.Add(new("Version", $"The instances run different SQL Server versions ({string.Join(", ", instances.Select(i => $"{i.Name}: {i.MajorVersion}"))}). A group needs the same version on every replica.", CheckLevel.Blocking));
        }

        if (instances.Select(i => i.Collation).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            checks.Add(new("Collation", $"The instances have different server collations ({string.Join(", ", instances.Select(i => $"{i.Name}: {i.Collation}"))}). A group needs the same collation on every replica.", CheckLevel.Blocking));
        }

        if (instances.Any(i => i.EngineEdition == 2) && instances.Any(i => i.EngineEdition == 3))
        {
            checks.Add(new("Edition", "Standard and Enterprise/Developer instances are mixed. A basic group is only supported between Standard edition servers, and a full group needs Enterprise on every replica.", CheckLevel.Blocking));
        }

        var basicOnly = instances.Any(i => i.EngineEdition == 2);
        if (basicOnly)
        {
            checks.Add(new("Edition", "Standard edition: only a basic availability group — exactly two replicas, one database, no readable secondary, no backups on the secondary.", CheckLevel.Warning));
            if (instances.Count > 2)
            {
                checks.Add(new("Edition", "A basic availability group has exactly two replicas.", CheckLevel.Blocking));
            }
        }

        var clusterTypes = new List<string>();
        var clusters = instances.Select(i => i.WsfcCluster).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (clusters is [{ } cluster])
        {
            clusterTypes.Add("WSFC");
            checks.Add(new("Cluster", $"Every instance is a node of Windows failover cluster {cluster}: automatic failover is possible.", CheckLevel.Ok));
        }
        else
        {
            checks.Add(new("Cluster", clusters.Count == 1
                ? "No instance is part of a Windows failover cluster, so a WSFC group — the only kind with automatic failover — is not possible. DataTray does not create clusters."
                : "The instances are not all nodes of the same Windows failover cluster, so a WSFC group is not possible.", CheckLevel.Warning));
        }

        if (instances.Count > 0 && instances.All(i => i.MajorVersion >= 14))
        {
            clusterTypes.Add("NONE");
        }

        // EXTERNAL is never offered: it needs a Pacemaker cluster configured around the group, which DataTray
        // does not do and cannot check (Rick's decision, 2026-08-13).

        var endpoints = instances.Count(i => i.Endpoint is not null);
        var createEndpoints = endpoints == 0;
        if (endpoints > 0 && endpoints < instances.Count)
        {
            checks.Add(new("Endpoints", $"Only {string.Join(", ", instances.Where(i => i.Endpoint is not null).Select(i => i.Name))} has a database mirroring endpoint. DataTray creates endpoints on all replicas or uses existing ones on all, never a mix — drop the existing one or create the missing ones first.", CheckLevel.Blocking));
        }
        else if (endpoints > 0)
        {
            checks.Add(new("Endpoints", "Every instance already has a database mirroring endpoint; they are used as they are. If a join fails with ANONYMOUS LOGON, their authentication does not match.", CheckLevel.Warning));
            foreach (var i in instances.Where(i => i.Endpoint is { State: not "STARTED" }))
            {
                checks.Add(new(i.Name, $"Its endpoint {i.Endpoint?.Name} is {i.Endpoint?.State}, not STARTED.", CheckLevel.Blocking));
            }
        }
        else
        {
            checks.Add(new("Endpoints", "No instance has a database mirroring endpoint yet. DataTray creates one on each, authenticated by certificate, and exchanges the public certificates between them.", CheckLevel.Ok));
        }

        var secondaries = instances.Skip(1).ToList();
        var databases = primaryDatabases.Select(d => (d.Name, Why: Ineligible(d, secondaries))).ToList();
        if (databases.All(d => d.Why is not null))
        {
            checks.Add(new("Databases", "No database on the primary can join a group yet — see the reasons listed with each one.", CheckLevel.Blocking));
        }

        return new Preflight(checks, clusterTypes, basicOnly, createEndpoints, databases);
    }

    private static string? Ineligible(DatabaseFacts d, IReadOnlyList<InstanceFacts> secondaries)
    {
        if (d.InAvailabilityGroup) return "already in an availability group";
        if (!string.Equals(d.State, "ONLINE", StringComparison.OrdinalIgnoreCase)) return $"{d.State.ToLowerInvariant()}, not online";
        if (!string.Equals(d.RecoveryModel, "FULL", StringComparison.OrdinalIgnoreCase)) return $"{d.RecoveryModel.ToLowerInvariant()} recovery model; a group needs FULL";
        if (!d.HasFullBackup) return "has never had a full backup";
        if (d.ReadOnly) return "read-only";
        if (d.AutoClose) return "AUTO_CLOSE is on";
        if (!string.Equals(d.UserAccess, "MULTI_USER", StringComparison.OrdinalIgnoreCase)) return $"{d.UserAccess.ToLowerInvariant()} access";
        if (d.Mirrored) return "configured for database mirroring";
        return secondaries.FirstOrDefault(s => s.Databases.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) is { } clash
            ? $"a database with this name already exists on {clash.Name}; automatic seeding needs it absent"
            : null;
    }

    /// <summary>Why these choices cannot be planned, or null when they can. The view shows it in place of the
    /// plan; <c>ExecuteAsync</c> refuses on it.</summary>
    public static string? Validate(NewGroupChoices c, Preflight preflight)
    {
        if (string.IsNullOrWhiteSpace(c.Group) || c.Group.Length > 128)
        {
            return "Give the group a name (at most 128 characters).";
        }

        if (!preflight.ClusterTypes.Contains(c.ClusterType, StringComparer.OrdinalIgnoreCase))
        {
            return "Choose a cluster type.";
        }

        if (c.Databases.Count == 0)
        {
            return "Pick at least one database.";
        }

        if (preflight.BasicOnly && c.Databases.Count != 1)
        {
            return "A basic availability group (Standard edition) holds exactly one database.";
        }

        var eligible = preflight.Databases.Where(d => d.Why is null).Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (c.Databases.FirstOrDefault(d => !eligible.Contains(d)) is { } bad)
        {
            return $"{bad} cannot join a group.";
        }

        if (c.Replicas.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.EndpointHost)) is { } noHost)
        {
            return $"Give the endpoint host for {noHost.Name}.";
        }

        return c.EndpointPort is < 1 or > 65535 ? "The endpoint port must be between 1 and 65535." : null;
    }

    /// <summary>The secret names a plan for these replicas asks for — generated once in the view, carried to
    /// the run, so the script the user reviewed is byte-for-byte the one that runs.</summary>
    public static IEnumerable<string> SecretNames(IReadOnlyList<string> replicas) =>
        replicas.Select(r => $"masterkey:{r}")
            .Concat(replicas.SelectMany(to => replicas.Where(from => from != to).Select(from => $"login:{from}@{to}")));

    /// <summary>A password SQL Server's policy accepts: 32 random base64 characters plus one of each class.</summary>
    public static string NewPassword() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)) + "aA1!";

    /// <summary>
    /// The plan, per instance and in order. <paramref name="secrets"/> supplies the passwords a plan needs
    /// (master keys, certificate logins) — generated by the caller so the plan itself stays deterministic and
    /// testable. A step whose SQL holds <c>$(cert:NAME)</c> is filled in at run time with the public
    /// certificate a <see cref="AgStep.CaptureAs"/> step read from that replica.
    /// </summary>
    public static IReadOnlyList<AgStep> Plan(NewGroupChoices c, IReadOnlyList<InstanceFacts> instances, bool createEndpoints, Func<string, string> secrets)
    {
        var steps = new List<AgStep>();
        var primary = c.Replicas[0].Name;

        if (createEndpoints)
        {
            foreach (var r in c.Replicas)
            {
                var cert = CertName(r.Name);
                steps.Add(new AgStep(r.Name,
                    $"""
                    USE [master];
                    IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##')
                        CREATE MASTER KEY ENCRYPTION BY PASSWORD = {Lit(secrets($"masterkey:{r.Name}"))};
                    CREATE CERTIFICATE {Id(cert)} WITH SUBJECT = {Lit($"DataTray availability group endpoint {r.Name}")}, EXPIRY_DATE = '20361231';
                    CREATE ENDPOINT {Id(EndpointName)} STATE = STARTED
                        AS TCP (LISTENER_PORT = {c.EndpointPort})
                        FOR DATABASE_MIRRORING (ROLE = ALL, AUTHENTICATION = CERTIFICATE {Id(cert)}, ENCRYPTION = REQUIRED ALGORITHM AES);
                    """,
                    "master key (if missing), endpoint certificate and endpoint"));
                steps.Add(new AgStep(r.Name, $"SELECT CERTENCODED(CERT_ID({Lit(cert)}));",
                    "read the endpoint's public certificate", CaptureAs: $"cert:{r.Name}"));
            }

            foreach (var to in c.Replicas)
            {
                foreach (var from in c.Replicas.Where(f => f != to))
                {
                    var cert = CertName(from.Name);
                    var login = $"{cert}_login";
                    steps.Add(new AgStep(to.Name,
                        $"""
                        USE [master];
                        CREATE LOGIN {Id(login)} WITH PASSWORD = {Lit(secrets($"login:{from.Name}@{to.Name}"))};
                        CREATE USER {Id(login)} FOR LOGIN {Id(login)};
                        CREATE CERTIFICATE {Id(cert)} AUTHORIZATION {Id(login)} FROM BINARY = $(cert:{from.Name});
                        GRANT CONNECT ON ENDPOINT::{Id(EndpointName)} TO {Id(login)};
                        """,
                        $"trust {from.Name}'s endpoint certificate"));
                }
            }
        }

        var clusterType = c.ClusterType.ToUpperInvariant();
        var options = new List<string> { $"CLUSTER_TYPE = {clusterType}", $"AUTOMATED_BACKUP_PREFERENCE = {c.BackupPreference}" };
        if (instances.Any(i => i.EngineEdition == 2))
        {
            options.Add("BASIC");
        }
        else
        {
            options.Add("DB_FAILOVER = ON");
        }

        var replicaClauses = c.Replicas.Select(r =>
        {
            var port = instances.First(i => i.Name == r.Name).Endpoint?.Port ?? c.EndpointPort;
            var failover = clusterType == "WSFC" && r.Synchronous && r.AutomaticFailover ? "AUTOMATIC" : "MANUAL";
            return $"""
                    {Lit(r.Name)} WITH (
                            ENDPOINT_URL = {Lit($"tcp://{r.EndpointHost}:{port}")},
                            AVAILABILITY_MODE = {(r.Synchronous ? "SYNCHRONOUS_COMMIT" : "ASYNCHRONOUS_COMMIT")},
                            FAILOVER_MODE = {failover},
                            SEEDING_MODE = AUTOMATIC)
                    """;
        });
        steps.Add(new AgStep(primary,
            $"""
            CREATE AVAILABILITY GROUP {Id(c.Group)}
                WITH ({string.Join(", ", options)})
                FOR DATABASE {string.Join(", ", c.Databases.Select(Id))}
                REPLICA ON
                    {string.Join(",\n        ", replicaClauses)};
            """,
            "create the group, with this instance as its primary"));

        foreach (var r in c.Replicas.Skip(1))
        {
            // Retried on its own: the secondary may not reach the new group's endpoint the instant it exists.
            // WSFC takes the plain form, which is also the only one SQL Server 2016 knows.
            var join = clusterType == "WSFC" ? "JOIN" : $"JOIN WITH (CLUSTER_TYPE = {clusterType})";
            steps.Add(new AgStep(r.Name, $"ALTER AVAILABILITY GROUP {Id(c.Group)} {join};", "join the group", Retries: true));
            steps.Add(new AgStep(r.Name, $"ALTER AVAILABILITY GROUP {Id(c.Group)} GRANT CREATE ANY DATABASE;",
                "let automatic seeding create the group's databases here"));
        }

        return steps;
    }

    /// <summary>The per-replica certificate name. Replica names are server names, which may hold a
    /// backslash (a named instance); that is valid inside a bracketed identifier.</summary>
    internal static string CertName(string replica) => $"datatray_ag_{replica}";
}
