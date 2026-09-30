using System.Text;
using DataTray.Providers.MsSql;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>What a failover to one replica turns out to be, decided by the group's cluster type and the
/// target's state — never by the operating system either side runs on.</summary>
internal enum FailoverKind
{
    /// <summary>WSFC, target synchronous-commit and failover-ready: one <c>FAILOVER</c>, no data loss.</summary>
    Planned,

    /// <summary>WSFC, target not failover-ready: <c>FORCE_FAILOVER_ALLOW_DATA_LOSS</c>.</summary>
    Forced,

    /// <summary><c>CLUSTER_TYPE = NONE</c>: an ordered sequence across two instances (Msg 47122 refuses a
    /// plain FAILOVER there).</summary>
    ReadScale,

    /// <summary>Nothing DataTray will run — <see cref="FailoverPlan.RefusalReason"/> says why.</summary>
    Refused
}

internal sealed record AgReplica(string Name, bool IsSynchronousCommit, bool? IsConnected);

/// <summary>One database on one replica. <see cref="SyncState"/> and <see cref="LastCommit"/> are null when
/// the instance the state was read from cannot see that replica's rows (a secondary only sees its own).</summary>
internal sealed record AgDatabase(string Name, string Replica, string? SyncState, bool IsFailoverReady, DateTime? LastCommit);

/// <summary>A listener, and the static addresses it was created with. No addresses means DHCP.</summary>
internal sealed record AgListener(string DnsName, int Port, IReadOnlyList<(string Address, string Mask)> Addresses);

/// <summary>Everything the planner needs, read from the group's DMVs (see <see cref="AvailabilityGroupQueries"/>).
/// <see cref="Primary"/> is null when the instance it was read from does not know — a WSFC primary that is
/// down, say.</summary>
internal sealed record AgTopology(
    string Group,
    string? ClusterType,
    string? Primary,
    int RequiredSynchronizedSecondaries,
    IReadOnlyList<AgReplica> Replicas,
    IReadOnlyList<AgDatabase> Databases,
    AgListener? Listener);

/// <summary>One statement in a plan, and the replica whose instance runs it. A wait step
/// (<see cref="IsWait"/>) is a query polled until it returns 0, and aborts the plan if it never does. A
/// <see cref="Retries"/> step is re-run for a short while when it fails, for a statement the instance only
/// accepts once it has caught up with the step before it.</summary>
internal sealed record AgStep(string Replica, string Sql, string Purpose, bool IsWait = false, bool Retries = false);

internal sealed record FailoverPlan(
    FailoverKind Kind,
    IReadOnlyList<AgStep> Steps,
    IReadOnlyList<string> Warnings,
    string? RefusalReason = null,
    string? ExternalCommands = null)
{
    /// <summary>Whether the plan can lose committed transactions. Only a forced WSFC failover can: the
    /// read-scale sequence uses the same statement, but only after its wait step proved the target is
    /// SYNCHRONIZED with the primary already offline.</summary>
    public bool CanLoseData => Kind == FailoverKind.Forced;

    /// <summary>Whether running it needs the user to type the group name. Everything but a planned failover:
    /// a read-scale failover takes the group offline for its duration, a forced one may lose data.</summary>
    public bool NeedsTypedConfirmation => Kind is FailoverKind.Forced or FailoverKind.ReadScale;

    /// <summary>The plan as the script the user reviews — also what <c>ExecuteAsync</c> compares against the
    /// plan it rebuilds from fresh state, so a plan that changed since review is refused, not run.</summary>
    public string Script()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            sb.Append("-- ").Append(i + 1).Append(". on ").Append(step.Replica).Append(": ").AppendLine(step.Purpose);
            sb.AppendLine(step.Sql.TrimEnd());
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Builds the failover plan for moving an availability group's primary role to <c>target</c> (SE-247).
/// Pure — the plan is data, so every branch is tested without a server. Which branch is taken follows
/// <c>cluster_type_desc</c> only:
/// <list type="bullet">
/// <item><c>WSFC</c> — one statement on the target: <c>FAILOVER</c> when it is synchronous-commit and every
/// database is failover-ready, otherwise <c>FORCE_FAILOVER_ALLOW_DATA_LOSS</c>.</item>
/// <item><c>NONE</c> (read-scale) — the documented sequence, every step visible.</item>
/// <item><c>EXTERNAL</c> (Pacemaker) — refused: the clustermanager owns the role and DataTray does not run
/// shell commands on cluster nodes (Rick's decision, 2026-08-13). The exact command is handed over instead.</item>
/// </list>
/// </summary>
internal static class AvailabilityGroupFailover
{
    public static FailoverPlan Plan(AgTopology t, string target)
    {
        var replica = t.Replicas.FirstOrDefault(r => SameName(r.Name, target));
        if (replica is null)
        {
            return Refuse($"{target} is not a replica of {t.Group}.");
        }

        if (t.Primary is not null && SameName(t.Primary, target))
        {
            return Refuse($"{target} is already the primary replica of {t.Group}.");
        }

        // Case-insensitive on purpose: sys.availability_groups returns this column lowercase ("none") where
        // the documentation shows uppercase — see Codebase gotchas and AvailabilityGroupStatus.
        return t.ClusterType?.ToUpperInvariant() switch
        {
            "WSFC" => Wsfc(t, replica),
            "NONE" => ReadScale(t, replica),
            "EXTERNAL" => External(t, replica),
            _ => Refuse($"Unknown cluster type '{t.ClusterType}'. DataTray only fails over WSFC and read-scale (NONE) groups.")
        };
    }

    private static FailoverPlan Wsfc(AgTopology t, AgReplica target)
    {
        var databases = t.Databases.Where(d => SameName(d.Replica, target.Name)).ToList();
        // A planned failover needs both ends synchronous-commit; is_failover_ready covers the target's state.
        var primary = t.Primary is null ? null : t.Replicas.FirstOrDefault(r => SameName(r.Name, t.Primary));
        var ready = target.IsSynchronousCommit && primary?.IsSynchronousCommit != false
                    && databases.Count > 0 && databases.All(d => d.IsFailoverReady);
        if (ready)
        {
            return new FailoverPlan(FailoverKind.Planned,
                [new AgStep(target.Name, $"ALTER AVAILABILITY GROUP {Id(t.Group)} FAILOVER;",
                    "planned manual failover — the target is synchronized, nothing is lost")],
                [$"Sessions on {t.Primary ?? "the current primary"} are disconnected. Clients that connect by instance name rather than through the listener do not follow the new primary."]);
        }

        var warnings = new List<string>
        {
            target.IsSynchronousCommit
                ? $"{target.Name} is not failover-ready: at least one database is not SYNCHRONIZED. Transactions the primary committed but {target.Name} has not received are lost."
                : $"{target.Name} uses asynchronous commit, so it can trail the primary. Transactions the primary committed but {target.Name} has not received are lost."
        };
        warnings.AddRange(LossPerDatabase(t, target.Name));
        warnings.Add("The Windows cluster must have quorum, or the failover is refused.");
        warnings.Add("Afterwards every secondary database is suspended, the old primary's included, and has to be resumed on its own replica. Resuming rolls back the transactions the new primary never got — if you might need them, create a database snapshot of each suspended database before you resume it.");

        return new FailoverPlan(FailoverKind.Forced,
            [new AgStep(target.Name, $"ALTER AVAILABILITY GROUP {Id(t.Group)} FORCE_FAILOVER_ALLOW_DATA_LOSS;",
                "forced failover — ALLOWS DATA LOSS")],
            warnings);
    }

    private static FailoverPlan ReadScale(AgTopology t, AgReplica target)
    {
        if (t.Primary is null)
        {
            return Refuse("The primary replica could not be determined. A read-scale failover takes the primary offline first, so it needs a working connection to it.");
        }

        if (t.Replicas.Count != 2)
        {
            // ponytail: two replicas only — the documented sequence, and what the lab verifies. With more,
            // every other secondary needs its own role change and resume; add when a real 3-replica read-scale
            // group asks for it.
            return Refuse($"{t.Group} has {t.Replicas.Count} replicas. DataTray only fails over a read-scale group of exactly two.");
        }

        if (target.IsConnected == false)
        {
            return Refuse($"{target.Name} is not connected to the primary, so it can never become SYNCHRONIZED. Fix the connection first.");
        }

        var group = Id(t.Group);
        var primary = t.Primary;
        var steps = new List<AgStep>();

        foreach (var r in t.Replicas.Where(r => !r.IsSynchronousCommit))
        {
            steps.Add(new AgStep(primary,
                $"ALTER AVAILABILITY GROUP {group} MODIFY REPLICA ON {Lit(r.Name)} WITH (AVAILABILITY_MODE = SYNCHRONOUS_COMMIT);",
                $"make {r.Name} synchronous-commit"));
        }

        steps.Add(new AgStep(primary, WaitForSynchronizedSql(t.Group, target.Name),
            $"wait until every database on {target.Name} is SYNCHRONIZED (0 = ready)", IsWait: true));
        steps.Add(new AgStep(primary,
            $"ALTER AVAILABILITY GROUP {group} SET (REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT = 1);",
            "refuse commits the secondary has not hardened"));
        steps.Add(new AgStep(primary, $"ALTER AVAILABILITY GROUP {group} OFFLINE;",
            "take the group offline on the primary — writes stop here"));
        steps.Add(new AgStep(target.Name, $"ALTER AVAILABILITY GROUP {group} FORCE_FAILOVER_ALLOW_DATA_LOSS;",
            "promote the target (the only failover CLUSTER_TYPE = NONE accepts; nothing is lost after the wait above)"));
        // Retried: issued the moment the promotion returns, the old primary is still RESOLVING and fails it
        // with "the availability group resource did not come online" — seen against the lab, and the same
        // statement succeeds a few seconds later.
        steps.Add(new AgStep(primary, $"ALTER AVAILABILITY GROUP {group} SET (ROLE = SECONDARY);",
            "demote the old primary (retried while it is still resolving)", Retries: true));
        // On the old primary, now a secondary. The read-scale page says "on the primary", but "Resume an
        // availability database" says a locally suspended secondary database is resumed on its own replica —
        // and the lab agrees: the old primary's databases are the suspended ones, and resuming them there works.
        foreach (var db in t.Databases.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            steps.Add(new AgStep(primary, $"ALTER DATABASE {Id(db)} SET HADR RESUME;",
                $"resume data movement for {db}"));
        }

        if (t.Listener is { } listener)
        {
            steps.Add(new AgStep(target.Name, $"ALTER AVAILABILITY GROUP {group} REMOVE LISTENER {Lit(listener.DnsName)};",
                "drop the listener, which no cluster moves for a read-scale group"));
            steps.Add(new AgStep(target.Name, AddListenerSql(t.Group, listener),
                "re-create it on the new primary"));
        }

        var warnings = new List<string>
        {
            $"The group is offline from step \"OFFLINE\" until {target.Name} is promoted: no writes are accepted anywhere in between.",
            "The plan stops before OFFLINE if the target does not reach SYNCHRONIZED within the wait, and changes nothing after that point.",
            $"Both replicas stay synchronous-commit and REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT stays 1 afterwards (it was {t.RequiredSynchronizedSecondaries}). With it at 1, the new primary stops accepting commits whenever the secondary is disconnected."
        };

        return new FailoverPlan(FailoverKind.ReadScale, steps, warnings);
    }

    private static FailoverPlan External(AgTopology t, AgReplica target) => new(
        FailoverKind.Refused, [], [],
        $"{t.Group} is managed by Pacemaker (CLUSTER_TYPE = EXTERNAL). The cluster manager owns which replica is primary, and a failover through T-SQL would fight it — DataTray does not run commands on cluster nodes. Run one of these on any cluster node:",
        PacemakerCommands(target.Name));

    /// <summary>The Pacemaker commands from "Always On availability group failover on Linux" (Microsoft Learn,
    /// failover-high-availability), with the target filled in. Neither the resource name nor the Pacemaker node
    /// name is visible from SQL Server, so the resource stays the documentation's <c>ag_cluster</c> and the node
    /// is the replica's name — both marked as the things to check before running anything.</summary>
    internal static string PacemakerCommands(string node) =>
        $"""
        # Check first: the AG resource name (the docs use ag_cluster) and the node name ({node} is the replica's
        # name, not necessarily Pacemaker's) — see: sudo pcs status  /  crm status
        # The target must be a synchronous-commit replica.

        # RHEL 8+ / Ubuntu, promotable clone (ag_cluster-clone):
        sudo pcs resource move ag_cluster-clone --master {node} && sleep 30 && sudo pcs resource clear ag_cluster-clone

        # RHEL 7 / older Ubuntu (ag_cluster-master):
        sudo pcs resource move ag_cluster-master {node} --master --lifetime=30S
        sudo pcs resource clear ag_cluster-master

        # SLES (not supported from SQL Server 2025 on):
        sudo crm resource migrate ag_cluster {node} --lifetime=30S
        sudo crm configure delete cli-prefer-ms-ag_cluster
        """;

    /// <summary>0 when every database in the group is SYNCHRONIZED on <paramref name="replica"/>. Counts the
    /// group's databases rather than the non-synchronized rows, so a database with no row for the target at
    /// all (never joined there) counts as not ready instead of vanishing from the check.</summary>
    internal static string WaitForSynchronizedSql(string group, string replica) =>
        $"""
        SELECT COUNT(*) FROM sys.availability_databases_cluster adc
        JOIN sys.availability_groups ag ON ag.group_id = adc.group_id
        WHERE ag.name = {Lit(group)} AND NOT EXISTS (
            SELECT 1 FROM sys.dm_hadr_database_replica_states drs
            JOIN sys.availability_replicas ar ON ar.replica_id = drs.replica_id
            WHERE ar.replica_server_name = {Lit(replica)} AND drs.group_database_id = adc.group_database_id
              AND drs.synchronization_state_desc = 'SYNCHRONIZED');
        """;

    private static string AddListenerSql(string group, AgListener listener)
    {
        var ip = listener.Addresses.Count == 0
            ? "DHCP"
            // An IPv6 address has no mask and is written as a one-element tuple.
            : $"IP ({string.Join(", ", listener.Addresses.Select(a => a.Mask.Length == 0 ? $"({Lit(a.Address)})" : $"({Lit(a.Address)}, {Lit(a.Mask)})"))})";
        return $"ALTER AVAILABILITY GROUP {Id(group)} ADD LISTENER {Lit(listener.DnsName)} (WITH {ip}, PORT = {listener.Port});";
    }

    /// <summary>Per database, how far the target trails the primary — the "how bad" number the user needs
    /// before accepting data loss. Same derivation the SE-284 dashboard shows (<see cref="AvailabilityGroupStatus.BehindBy"/>).</summary>
    private static IEnumerable<string> LossPerDatabase(AgTopology t, string target)
    {
        foreach (var db in t.Databases.Where(d => SameName(d.Replica, target)))
        {
            var primary = t.Primary is null ? null : t.Databases.FirstOrDefault(d => SameName(d.Replica, t.Primary) && SameName(d.Name, db.Name));
            var behind = AvailabilityGroupStatus.BehindBy(false, primary?.LastCommit, db.LastCommit);
            var state = db.SyncState ?? "state unknown";
            yield return behind is null
                ? $"{db.Name}: {state}, amount behind unknown (the primary's last commit time is not visible from here)."
                : $"{db.Name}: {state}, {behind} behind the primary.";
        }
    }

    private static FailoverPlan Refuse(string reason) => new(FailoverKind.Refused, [], [], reason);

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static string Id(string name) => $"[{name.Replace("]", "]]")}]";

    internal static string Lit(string value) => $"N'{value.Replace("'", "''")}'";
}
