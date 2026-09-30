using DataTray.Tools.MsSqlAdmin;

namespace DataTray.Tools.MsSqlAdmin.Tests;

/// <summary>
/// The failover plan is the part of SE-247 that runs destructive statements on production instances, and the
/// only part a test can reach: no fixture here has an availability group, let alone a WSFC or Pacemaker one.
/// So every branch the cluster type and the target's state can take is pinned as data — which statement,
/// on which instance, in which order, and what the user is told.
/// </summary>
public class AvailabilityGroupFailoverTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0);

    private static AgTopology Topology(
        string clusterType,
        bool targetSync = true,
        bool targetReady = true,
        string? primary = "n1",
        AgListener? listener = null,
        int replicas = 2,
        bool primarySync = true) =>
        new("ag1", clusterType, primary, 0,
            [
                new AgReplica("n1", primarySync, true),
                new AgReplica("n2", targetSync, true),
                .. Enumerable.Range(3, Math.Max(0, replicas - 2)).Select(i => new AgReplica($"n{i}", true, true))
            ],
            [
                new AgDatabase("Sales", "n1", "SYNCHRONIZED", true, Now),
                new AgDatabase("Sales", "n2", targetReady ? "SYNCHRONIZED" : "SYNCHRONIZING", targetReady, Now.AddSeconds(-41)),
                new AgDatabase("Audit", "n1", "SYNCHRONIZED", true, Now),
                new AgDatabase("Audit", "n2", targetReady ? "SYNCHRONIZED" : "SYNCHRONIZING", targetReady, Now.AddMinutes(-104))
            ],
            listener);

    // ── WSFC ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Wsfc_with_a_synchronized_target_is_one_plain_FAILOVER_on_the_target()
    {
        var plan = AvailabilityGroupFailover.Plan(Topology("WSFC"), "n2");

        Assert.Equal(FailoverKind.Planned, plan.Kind);
        var step = Assert.Single(plan.Steps);
        Assert.Equal("n2", step.Replica);
        Assert.Equal("ALTER AVAILABILITY GROUP [ag1] FAILOVER;", step.Sql);
        Assert.False(plan.CanLoseData);
        Assert.False(plan.NeedsTypedConfirmation);
    }

    [Fact]
    public void Wsfc_with_an_asynchronous_target_is_forced_and_names_the_loss_per_database()
    {
        var plan = AvailabilityGroupFailover.Plan(Topology("WSFC", targetSync: false, targetReady: false), "n2");

        Assert.Equal(FailoverKind.Forced, plan.Kind);
        var step = Assert.Single(plan.Steps);
        Assert.Equal("n2", step.Replica);
        Assert.Equal("ALTER AVAILABILITY GROUP [ag1] FORCE_FAILOVER_ALLOW_DATA_LOSS;", step.Sql);
        Assert.True(plan.CanLoseData);
        Assert.True(plan.NeedsTypedConfirmation);
        Assert.Contains(plan.Warnings, w => w.Contains("asynchronous commit"));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Sales:") && w.Contains("41s behind"));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Audit:") && w.Contains("1h 44m behind"));
        Assert.Contains(plan.Warnings, w => w.Contains("database snapshot"));
        Assert.Contains(plan.Warnings, w => w.Contains("quorum"));
    }

    [Fact]
    public void Wsfc_with_a_synchronous_target_that_is_not_failover_ready_is_still_forced()
    {
        // Synchronous-commit is a setting; SYNCHRONIZED is a state. Only the state makes FAILOVER loss-free.
        var plan = AvailabilityGroupFailover.Plan(Topology("WSFC", targetSync: true, targetReady: false), "n2");

        Assert.Equal(FailoverKind.Forced, plan.Kind);
        Assert.Contains(plan.Warnings, w => w.Contains("not failover-ready"));
    }

    [Fact]
    public void Wsfc_with_an_asynchronous_primary_is_not_a_planned_failover()
    {
        // The documented precondition is both ends synchronous-commit, not only the target.
        var plan = AvailabilityGroupFailover.Plan(Topology("WSFC", primarySync: false), "n2");

        Assert.Equal(FailoverKind.Forced, plan.Kind);
    }

    [Fact]
    public void Wsfc_with_the_primary_down_says_the_loss_is_unknown_rather_than_zero()
    {
        var t = Topology("WSFC", targetSync: false, targetReady: false, primary: null);

        var plan = AvailabilityGroupFailover.Plan(t, "n2");

        Assert.Equal(FailoverKind.Forced, plan.Kind);
        Assert.Contains(plan.Warnings, w => w.StartsWith("Sales:") && w.Contains("amount behind unknown"));
    }

    [Theory]
    [InlineData("wsfc")]  // sys.availability_groups returns some _desc columns lowercase (Codebase gotchas)
    [InlineData("WSFC")]
    public void Cluster_type_is_matched_case_insensitively(string clusterType)
    {
        Assert.Equal(FailoverKind.Planned, AvailabilityGroupFailover.Plan(Topology(clusterType), "n2").Kind);
    }

    // ── NONE (read-scale) ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Read_scale_is_the_documented_sequence_on_the_right_instances()
    {
        var plan = AvailabilityGroupFailover.Plan(Topology("none", targetSync: false), "n2");

        Assert.Equal(FailoverKind.ReadScale, plan.Kind);
        Assert.False(plan.CanLoseData);
        Assert.True(plan.NeedsTypedConfirmation);
        Assert.Equal(
        [
            ("n1", "ALTER AVAILABILITY GROUP [ag1] MODIFY REPLICA ON N'n2' WITH (AVAILABILITY_MODE = SYNCHRONOUS_COMMIT);"),
            ("n1", "WAIT"),
            ("n1", "ALTER AVAILABILITY GROUP [ag1] SET (REQUIRED_SYNCHRONIZED_SECONDARIES_TO_COMMIT = 1);"),
            ("n1", "ALTER AVAILABILITY GROUP [ag1] OFFLINE;"),
            ("n2", "ALTER AVAILABILITY GROUP [ag1] FORCE_FAILOVER_ALLOW_DATA_LOSS;"),
            ("n1", "ALTER AVAILABILITY GROUP [ag1] SET (ROLE = SECONDARY);"),
            ("n1", "ALTER DATABASE [Audit] SET HADR RESUME;"),
            ("n1", "ALTER DATABASE [Sales] SET HADR RESUME;")
        ], plan.Steps.Select(s => (s.Replica, s.IsWait ? "WAIT" : s.Sql)).ToList());
    }

    [Fact]
    public void Read_scale_retries_only_the_demotion_of_the_old_primary()
    {
        // Against the lab, SET (ROLE = SECONDARY) issued straight after the promotion failed while the old
        // primary was still resolving, and succeeded seconds later. Nothing else is safe to repeat blindly.
        var plan = AvailabilityGroupFailover.Plan(Topology("NONE"), "n2");

        var retried = Assert.Single(plan.Steps, s => s.Retries);
        Assert.Equal("ALTER AVAILABILITY GROUP [ag1] SET (ROLE = SECONDARY);", retried.Sql);
    }

    [Fact]
    public void Read_scale_skips_the_mode_change_for_replicas_already_synchronous()
    {
        var plan = AvailabilityGroupFailover.Plan(Topology("NONE"), "n2");

        Assert.DoesNotContain(plan.Steps, s => s.Sql.Contains("MODIFY REPLICA"));
        Assert.True(plan.Steps[0].IsWait);
    }

    [Fact]
    public void Read_scale_waits_before_anything_irreversible()
    {
        var plan = AvailabilityGroupFailover.Plan(Topology("NONE", targetSync: false, primarySync: false), "n2");

        var wait = plan.Steps.ToList().FindIndex(s => s.IsWait);
        var offline = plan.Steps.ToList().FindIndex(s => s.Sql.EndsWith("OFFLINE;"));
        Assert.True(wait >= 0 && wait < offline);
        Assert.All(plan.Steps.Take(wait), s => Assert.Contains("MODIFY REPLICA", s.Sql));
        Assert.Contains("N'ag1'", plan.Steps[wait].Sql);
        Assert.Contains("N'n2'", plan.Steps[wait].Sql);
    }

    [Fact]
    public void Read_scale_re_creates_the_listener_on_the_new_primary()
    {
        var listener = new AgListener("ag1-listener", 1433, [("10.0.0.50", "255.255.255.0"), ("fd00::50", "")]);

        var plan = AvailabilityGroupFailover.Plan(Topology("NONE", listener: listener), "n2");

        Assert.Equal(("n2", "ALTER AVAILABILITY GROUP [ag1] REMOVE LISTENER N'ag1-listener';"), (plan.Steps[^2].Replica, plan.Steps[^2].Sql));
        Assert.Equal(("n2", "ALTER AVAILABILITY GROUP [ag1] ADD LISTENER N'ag1-listener' (WITH IP ((N'10.0.0.50', N'255.255.255.0'), (N'fd00::50')), PORT = 1433);"),
            (plan.Steps[^1].Replica, plan.Steps[^1].Sql));
    }

    [Fact]
    public void Read_scale_re_creates_a_dhcp_listener_as_dhcp()
    {
        var plan = AvailabilityGroupFailover.Plan(Topology("NONE", listener: new AgListener("l", 5022, [])), "n2");

        Assert.Equal("ALTER AVAILABILITY GROUP [ag1] ADD LISTENER N'l' (WITH DHCP, PORT = 5022);", plan.Steps[^1].Sql);
    }

    [Fact]
    public void Read_scale_refuses_without_a_known_primary()
    {
        var plan = AvailabilityGroupFailover.Plan(Topology("NONE", primary: null), "n2");

        Assert.Equal(FailoverKind.Refused, plan.Kind);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Read_scale_refuses_more_than_two_replicas()
    {
        var plan = AvailabilityGroupFailover.Plan(Topology("NONE", replicas: 3), "n2");

        Assert.Equal(FailoverKind.Refused, plan.Kind);
        Assert.Contains("3 replicas", plan.RefusalReason);
    }

    [Fact]
    public void Read_scale_refuses_a_disconnected_target()
    {
        var t = Topology("NONE") with { Replicas = [new AgReplica("n1", true, true), new AgReplica("n2", true, false)] };

        Assert.Equal(FailoverKind.Refused, AvailabilityGroupFailover.Plan(t, "n2").Kind);
    }

    // ── EXTERNAL (Pacemaker) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void External_is_refused_with_the_cluster_command_for_the_target_node_and_no_sql()
    {
        var plan = AvailabilityGroupFailover.Plan(Topology("external"), "n2");

        Assert.Equal(FailoverKind.Refused, plan.Kind);
        Assert.Empty(plan.Steps);
        Assert.Contains("Pacemaker", plan.RefusalReason);
        Assert.Contains("sudo pcs resource move ag_cluster-clone --master n2 && sleep 30 && sudo pcs resource clear ag_cluster-clone", plan.ExternalCommands);
        Assert.Contains("sudo pcs resource move ag_cluster-master n2 --master --lifetime=30S", plan.ExternalCommands);
        Assert.Contains("sudo crm resource migrate ag_cluster n2 --lifetime=30S", plan.ExternalCommands);
        Assert.Contains("cli-prefer-ms-ag_cluster", plan.ExternalCommands);
    }

    // ── Refusals and escaping ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("n1")]      // already primary
    [InlineData("nope")]    // not a replica
    public void A_target_that_cannot_take_over_is_refused(string target)
    {
        Assert.Equal(FailoverKind.Refused, AvailabilityGroupFailover.Plan(Topology("WSFC"), target).Kind);
    }

    [Fact]
    public void An_unknown_cluster_type_is_refused_rather_than_guessed()
    {
        Assert.Equal(FailoverKind.Refused, AvailabilityGroupFailover.Plan(Topology("SOMETHING_NEW"), "n2").Kind);
    }

    [Fact]
    public void Names_are_escaped_as_identifiers_and_literals()
    {
        var t = Topology("NONE", targetSync: false) with
        {
            Group = "a]g'1",
            Replicas = [new AgReplica("n1", true, true), new AgReplica("o'neil", false, true)],
            Databases = [new AgDatabase("x]y", "n1", "SYNCHRONIZED", true, Now)]
        };

        var plan = AvailabilityGroupFailover.Plan(t, "o'neil");

        Assert.Equal("ALTER AVAILABILITY GROUP [a]]g'1] MODIFY REPLICA ON N'o''neil' WITH (AVAILABILITY_MODE = SYNCHRONOUS_COMMIT);", plan.Steps[0].Sql);
        Assert.Contains("ag.name = N'a]g''1'", plan.Steps[1].Sql);
        Assert.Contains(plan.Steps, s => s.Sql == "ALTER DATABASE [x]]y] SET HADR RESUME;");
    }

    [Fact]
    public void The_reviewed_script_changes_when_a_planned_failover_turns_into_a_forced_one()
    {
        // ExecuteAsync refuses to run when the rebuilt script differs from the one the user reviewed.
        var reviewed = AvailabilityGroupFailover.Plan(Topology("WSFC"), "n2").Script();
        var now = AvailabilityGroupFailover.Plan(Topology("WSFC", targetReady: false), "n2").Script();

        Assert.NotEqual(reviewed, now);
        Assert.Equal("-- 1. on n2: planned manual failover — the target is synchronized, nothing is lost\nALTER AVAILABILITY GROUP [ag1] FAILOVER;",
            reviewed.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_tool_sits_on_the_group_node_and_goes_through_the_host_confirmation()
    {
        var tool = new FailoverAvailabilityGroupTool();

        Assert.Equal([DataTray.Sdk.Schema.DbNodeKind.AvailabilityGroup], tool.Target.NodeKinds ?? []);
        Assert.Equal(["sqlserver"], tool.Target.ProviderIds ?? []);
        Assert.True(tool.IsDestructive);
        Assert.True(tool.IsNodeAction);
    }

    // ── Parsing the DMV rows ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_reads_lowercase_descs_dbnull_and_small_ints_of_any_width()
    {
        var t = AvailabilityGroupQueries.Parse("ag1",
            [["none", (byte)1, "agnode1"]],
            [["agnode1", "SYNCHRONOUS_COMMIT", "CONNECTED", true], ["agnode2", "ASYNCHRONOUS_COMMIT", DBNull.Value, DBNull.Value]],
            [["AgDemo", "agnode2", "synchronizing", false, DBNull.Value]],
            [["ag-l", 1433, "10.0.0.5", "255.0.0.0", false], ["ag-l", 1433, "10.0.0.6", "255.0.0.0", true]]);

        Assert.Equal("none", t.ClusterType);
        Assert.Equal("agnode1", t.Primary);
        Assert.Equal(1, t.RequiredSynchronizedSecondaries);
        Assert.True(t.Replicas[0].IsSynchronousCommit);
        Assert.False(t.Replicas[1].IsSynchronousCommit);
        Assert.Null(t.Replicas[1].IsConnected);   // a secondary cannot see other replicas' state — unknown, not "down"
        Assert.Equal("SYNCHRONIZING", t.Databases[0].SyncState);
        Assert.Null(t.Databases[0].LastCommit);
        Assert.NotNull(t.Listener);
        Assert.Equal([("10.0.0.5", "255.0.0.0")], t.Listener.Addresses);  // the DHCP row is not a static address
    }

    [Fact]
    public void Parse_refuses_a_group_that_does_not_exist()
    {
        Assert.Throws<InvalidOperationException>(() => AvailabilityGroupQueries.Parse("ag1", [], [], [], []));
    }

    [Fact]
    public void The_local_replica_is_the_row_flagged_is_local()
    {
        Assert.Equal("agnode2", AvailabilityGroupQueries.LocalReplica([["agnode1", "S", null, DBNull.Value], ["agnode2", "S", "CONNECTED", true]]));
        Assert.Null(AvailabilityGroupQueries.LocalReplica([["agnode1", "S", null, false]]));
    }
}
