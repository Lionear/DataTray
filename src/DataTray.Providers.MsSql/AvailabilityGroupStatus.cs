namespace DataTray.Providers.MsSql;

/// <summary>
/// Decodes/derives what the Always On DMVs report into the labels SE-284's dashboard shows. Public and
/// pure — same shape as <see cref="AgentJobStatus"/> — so the parts worth trusting without a live
/// availability group are covered by tests instead of eyeballed against a real one, which this repo's
/// test suite has no fixture for at all (see the MSSQL single-instance testcontainer fixture, which
/// covers "no AG present" and nothing about DMV content).
/// </summary>
public static class AvailabilityGroupStatus
{
    /// <summary>
    /// How far a secondary trails the primary, derived from each side's own <c>last_commit_time</c> — the
    /// primary's minus this replica's. Reported in seconds/minutes/hours, not the DMV's
    /// <c>log_send_queue_size</c>/<c>redo_queue_size</c> (kilobytes): bytes explain <i>why</i> a replica is
    /// behind (there is unsent or unreplayed log), seconds say <i>how bad</i> it is right now, which is the
    /// number a person opening this dashboard can act on. Null for the primary's own row (nothing to be
    /// behind) or when either timestamp is unknown.
    /// </summary>
    public static string? BehindBy(bool isPrimaryRow, DateTime? primaryLastCommit, DateTime? replicaLastCommit)
    {
        if (isPrimaryRow || primaryLastCommit is null || replicaLastCommit is null)
        {
            return null;
        }

        var behind = primaryLastCommit.Value - replicaLastCommit.Value;
        if (behind <= TimeSpan.Zero)
        {
            return "0s";
        }

        var (h, m, s) = ((int)behind.TotalHours, behind.Minutes, behind.Seconds);
        return h > 0 ? $"{h}h {m:D2}m" : m > 0 ? $"{m}m {s:D2}s" : $"{s}s";
    }

    /// <summary>
    /// Whether a replica actually serves read traffic, from its role and — for a secondary —
    /// <c>secondary_role_allow_connections_desc</c>. The primary's own row is always "No" here: read-intent
    /// routing is what a secondary offers, not a setting the primary carries on itself.
    /// </summary>
    public static string Readable(string roleDesc, string? secondaryAllowConnections) => roleDesc switch
    {
        "PRIMARY" => "No",
        _ => secondaryAllowConnections switch
        {
            "ALL" => "Yes (all)",
            "READ_ONLY" => "Yes (read-intent)",
            _ => "No"
        }
    };

    /// <summary>The per-database "Suspended" column: plain "No", or "Yes" with the reason SQL Server gives
    /// for why data movement stopped — the detail someone needs before resuming it.</summary>
    public static string Suspended(bool isSuspended, string? reasonDesc) =>
        isSuspended ? $"Yes — {reasonDesc ?? "unknown reason"}" : "No";

    /// <summary>SSMS-style wording for <c>automated_backup_preference_desc</c> — SQL Server's own value is
    /// a constant name (<c>SECONDARY_ONLY</c>), not a sentence.</summary>
    public static string BackupPreferenceText(string? desc) => desc switch
    {
        "PRIMARY" => "Prefer primary",
        "SECONDARY_ONLY" => "Secondary only",
        "SECONDARY" => "Prefer secondary",
        "NONE" => "None",
        _ => desc ?? "Unknown"
    };

    /// <summary>
    /// The dashboard's top banner: whether the group's rolled-up <c>synchronization_health_desc</c> is worth
    /// a second look, and who is primary right now. Deliberately does not name a specific replica or a "how
    /// far behind" figure the way the SE-247 mockup's prose does — that would mean re-deriving which replica
    /// is the outlier here as well as in the Databases table below, and the two would eventually disagree.
    /// One derivation, shown once, in the table it actually belongs to.
    /// </summary>
    public static string Summary(string? primaryReplica, string? groupSyncHealthDesc)
    {
        var primary = primaryReplica ?? "unknown";
        return groupSyncHealthDesc switch
        {
            "HEALTHY" => $"Healthy. Primary is {primary}.",
            "PARTIALLY_HEALTHY" =>
                $"Primary is {primary}. At least one secondary is not fully synchronized — see Databases below for which, and by how much.",
            "NOT_HEALTHY" => $"Not healthy. Primary is {primary}. See Replicas below for which replica is failing.",
            _ => $"Primary is {primary}."
        };
    }
}
