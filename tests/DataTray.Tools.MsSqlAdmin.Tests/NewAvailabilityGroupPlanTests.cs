using DataTray.Tools.MsSqlAdmin;

namespace DataTray.Tools.MsSqlAdmin.Tests;

/// <summary>
/// The new-group wizard's gate and plan (SE-247). The gate is the part that keeps DataTray from offering
/// something the topology cannot do — one instance, no cluster, a Standard edition, half the endpoints —
/// so each of those is pinned here, next to the statements the plan generates for the cases that pass.
/// </summary>
public class NewAvailabilityGroupPlanTests
{
    private static InstanceFacts Instance(
        string name,
        int version = 16,
        int edition = 3,
        string collation = "SQL_Latin1_General_CP1_CI_AS",
        bool hadr = true,
        bool control = true,
        string? cluster = null,
        MirroringEndpoint? endpoint = null,
        params string[] databases) =>
        new(name, version, collation, edition, hadr, control, cluster, endpoint, databases);

    private static DatabaseFacts Db(
        string name,
        string state = "ONLINE",
        string recovery = "FULL",
        bool inAg = false,
        bool backup = true,
        bool autoClose = false,
        string access = "MULTI_USER",
        bool readOnly = false,
        bool mirrored = false) =>
        new(name, state, recovery, inAg, autoClose, access, readOnly, backup, mirrored);

    private static readonly MirroringEndpoint Endpoint = new("Hadr_endpoint", 5022, "STARTED", "CERTIFICATE");

    private static Preflight Check(IReadOnlyList<InstanceFacts> instances, params DatabaseFacts[] dbs) =>
        NewAvailabilityGroupPlan.Check(instances, dbs.Length == 0 ? [Db("Sales")] : dbs);

    // ── The gate ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void One_instance_is_blocked_before_any_form()
    {
        var preflight = Check([Instance("n1")]);

        Assert.False(preflight.CanProceed);
        Assert.Contains(preflight.Checks, c => c is { Subject: "Replicas", Level: CheckLevel.Blocking });
    }

    [Fact]
    public void Two_instances_without_a_cluster_offer_read_scale_only()
    {
        var preflight = Check([Instance("n1"), Instance("n2")]);

        Assert.True(preflight.CanProceed);
        Assert.Equal(["NONE"], preflight.ClusterTypes);
        Assert.Contains(preflight.Checks, c => c is { Subject: "Cluster", Level: CheckLevel.Warning } && c.Message.Contains("automatic failover"));
    }

    [Fact]
    public void Nodes_of_one_wsfc_offer_wsfc_first_and_read_scale_too()
    {
        var preflight = Check([Instance("n1", cluster: "CL1"), Instance("n2", cluster: "cl1")]);

        Assert.Equal(["WSFC", "NONE"], preflight.ClusterTypes);
    }

    [Fact]
    public void Nodes_of_different_clusters_do_not_offer_wsfc()
    {
        var preflight = Check([Instance("n1", cluster: "CL1"), Instance("n2", cluster: "CL2")]);

        Assert.DoesNotContain("WSFC", preflight.ClusterTypes);
    }

    [Fact]
    public void Sql_2016_without_a_cluster_has_nothing_to_offer()
    {
        // NONE needs 2017; with no WSFC either, there is no cluster type left, so the gate stays shut.
        var preflight = Check([Instance("n1", version: 13), Instance("n2", version: 13)]);

        Assert.Empty(preflight.ClusterTypes);
        Assert.False(preflight.CanProceed);
    }

    [Fact]
    public void External_is_never_offered()
    {
        Assert.DoesNotContain("EXTERNAL", Check([Instance("n1", cluster: "c"), Instance("n2", cluster: "c")]).ClusterTypes);
    }

    [Theory]
    [InlineData(4)]   // Express
    [InlineData(5)]   // Azure SQL Database
    public void An_edition_without_availability_groups_is_blocked(int edition)
    {
        Assert.False(Check([Instance("n1"), Instance("n2", edition: edition)]).CanProceed);
    }

    [Fact]
    public void Always_On_switched_off_is_blocked_and_says_where_to_switch_it_on()
    {
        var preflight = Check([Instance("n1"), Instance("n2", hadr: false)]);

        var check = Assert.Single(preflight.Checks, c => c.Subject == "n2");
        Assert.Equal(CheckLevel.Blocking, check.Level);
        Assert.Contains("Configuration Manager", check.Message);
        Assert.Contains("mssql-conf", check.Message);
    }

    [Fact]
    public void A_login_without_control_server_is_blocked()
    {
        Assert.False(Check([Instance("n1", control: false), Instance("n2")]).CanProceed);
    }

    [Fact]
    public void Two_connections_to_the_same_instance_are_blocked()
    {
        Assert.False(Check([Instance("n1"), Instance("N1")]).CanProceed);
    }

    [Fact]
    public void Different_versions_or_collations_are_blocked()
    {
        Assert.False(Check([Instance("n1", version: 15), Instance("n2", version: 16)]).CanProceed);
        Assert.False(Check([Instance("n1"), Instance("n2", collation: "Latin1_General_100_CI_AS_SC_UTF8")]).CanProceed);
    }

    [Fact]
    public void Standard_mixed_with_enterprise_is_blocked()
    {
        // Basic groups are for Standard servers only; a full group needs Enterprise everywhere.
        Assert.False(Check([Instance("n1", edition: 2), Instance("n2", edition: 3)]).CanProceed);
    }

    [Fact]
    public void Standard_edition_means_basic_and_exactly_two_replicas()
    {
        var two = Check([Instance("n1", edition: 2), Instance("n2", edition: 2)]);
        var three = Check([Instance("n1", edition: 2), Instance("n2", edition: 2), Instance("n3", edition: 2)]);

        Assert.True(two.BasicOnly);
        Assert.True(two.CanProceed);
        Assert.False(three.CanProceed);
    }

    [Fact]
    public void Endpoints_are_created_when_none_exist_and_reused_when_all_do()
    {
        Assert.True(Check([Instance("n1"), Instance("n2")]).CreateEndpoints);

        var reuse = Check([Instance("n1", endpoint: Endpoint), Instance("n2", endpoint: Endpoint)]);
        Assert.False(reuse.CreateEndpoints);
        Assert.True(reuse.CanProceed);
    }

    [Fact]
    public void Half_the_endpoints_is_blocked()
    {
        var preflight = Check([Instance("n1", endpoint: Endpoint), Instance("n2")]);

        Assert.False(preflight.CanProceed);
        Assert.Contains(preflight.Checks, c => c is { Subject: "Endpoints", Level: CheckLevel.Blocking } && c.Message.Contains("n1"));
    }

    [Fact]
    public void A_stopped_endpoint_is_blocked()
    {
        var stopped = Endpoint with { State = "STOPPED" };

        Assert.False(Check([Instance("n1", endpoint: Endpoint), Instance("n2", endpoint: stopped)]).CanProceed);
    }

    [Fact]
    public void Each_ineligible_database_says_why()
    {
        var preflight = Check([Instance("n1"), Instance("n2", databases: ["Clash"])],
            Db("Ok"), Db("InAg", inAg: true), Db("Simple", recovery: "SIMPLE"), Db("NoBackup", backup: false),
            Db("Off", state: "OFFLINE"), Db("Ro", readOnly: true), Db("Ac", autoClose: true), Db("Su", access: "SINGLE_USER"),
            Db("Clash"), Db("Mirror", mirrored: true));

        var why = preflight.Databases.ToDictionary(d => d.Name, d => d.Why);
        Assert.Null(why["Ok"]);
        Assert.Contains("already in an availability group", why["InAg"]);
        Assert.Contains("FULL", why["Simple"]);
        Assert.Contains("full backup", why["NoBackup"]);
        Assert.Contains("offline", why["Off"]);
        Assert.Contains("read-only", why["Ro"]);
        Assert.Contains("AUTO_CLOSE", why["Ac"]);
        Assert.Contains("single_user", why["Su"]);
        Assert.Contains("already exists on n2", why["Clash"]);
        Assert.Contains("database mirroring", why["Mirror"]);
        Assert.True(preflight.CanProceed);
    }

    [Fact]
    public void No_eligible_database_is_blocked()
    {
        Assert.False(Check([Instance("n1"), Instance("n2")], Db("Simple", recovery: "SIMPLE")).CanProceed);
    }

    // ── The plan ──────────────────────────────────────────────────────────────────────────────────────────

    private static readonly NewGroupChoices Choices = new("ag2", "NONE",
        [new ReplicaChoice("n1", "n1.lab", true, false), new ReplicaChoice("n2", "n2.lab", false, true)],
        ["Sales", "Audit"], "SECONDARY", 5022);

    private static string Secret(string key) => $"pw<{key}>";

    [Fact]
    public void Creating_endpoints_exchanges_certificates_in_both_directions_without_a_file()
    {
        var steps = NewAvailabilityGroupPlan.Plan(Choices, [Instance("n1"), Instance("n2")], createEndpoints: true, Secret);

        Assert.Equal(
        [
            ("n1", "setup"), ("n1", "capture"), ("n2", "setup"), ("n2", "capture"),
            ("n1", "trust n2"), ("n2", "trust n1"),
            ("n1", "create"), ("n2", "join"), ("n2", "grant")
        ], steps.Select(s => (s.Replica, Kind(s))).ToList());

        var trustN2OnN1 = steps[4].Sql;
        Assert.Contains("CREATE CERTIFICATE [datatray_ag_n2] AUTHORIZATION [datatray_ag_n2_login] FROM BINARY = $(cert:n2);", trustN2OnN1);
        Assert.Contains("GRANT CONNECT ON ENDPOINT::[Hadr_endpoint] TO [datatray_ag_n2_login];", trustN2OnN1);
        Assert.Contains("N'pw<login:n2@n1>'", trustN2OnN1);
        Assert.Equal("cert:n2", steps[3].CaptureAs);
        Assert.DoesNotContain(steps, s => s.Sql.Contains("BACKUP CERTIFICATE") || s.Sql.Contains("FROM FILE"));
        Assert.Contains("AUTHENTICATION = CERTIFICATE [datatray_ag_n1], ENCRYPTION = REQUIRED ALGORITHM AES", steps[0].Sql);
        Assert.Contains("LISTENER_PORT = 5022", steps[0].Sql);
        Assert.Contains("CREATE MASTER KEY ENCRYPTION BY PASSWORD = N'pw<masterkey:n1>'", steps[0].Sql);
    }

    private static string Kind(AgStep s) =>
        s.CaptureAs is not null ? "capture"
        : s.Sql.Contains("CREATE ENDPOINT") ? "setup"
        : s.Sql.Contains("GRANT CONNECT") ? "trust " + s.Purpose.Split(' ')[1][..^2]
        : s.Sql.StartsWith("CREATE AVAILABILITY GROUP") ? "create"
        : s.Sql.Contains(" JOIN") ? "join"
        : s.Sql.Contains("GRANT CREATE ANY DATABASE") ? "grant"
        : "?";

    [Fact]
    public void Reusing_endpoints_skips_certificates_and_takes_each_endpoint_port()
    {
        var instances = new[] { Instance("n1", endpoint: Endpoint), Instance("n2", endpoint: Endpoint with { Port = 5033 }) };

        var steps = NewAvailabilityGroupPlan.Plan(Choices, instances, createEndpoints: false, Secret);

        Assert.Equal(["create", "join", "grant"], steps.Select(Kind).ToList());
        Assert.Contains("ENDPOINT_URL = N'tcp://n2.lab:5033'", steps[0].Sql);
        Assert.Contains("ENDPOINT_URL = N'tcp://n1.lab:5022'", steps[0].Sql);
    }

    [Fact]
    public void Create_carries_the_choices_and_seeds_automatically()
    {
        var sql = NewAvailabilityGroupPlan.Plan(Choices, [Instance("n1"), Instance("n2")], false, Secret)[0].Sql;

        Assert.Contains("CREATE AVAILABILITY GROUP [ag2]", sql);
        Assert.Contains("WITH (CLUSTER_TYPE = NONE, AUTOMATED_BACKUP_PREFERENCE = SECONDARY, DB_FAILOVER = ON)", sql);
        Assert.Contains("FOR DATABASE [Sales], [Audit]", sql);
        Assert.Contains("AVAILABILITY_MODE = SYNCHRONOUS_COMMIT", sql);
        Assert.Contains("AVAILABILITY_MODE = ASYNCHRONOUS_COMMIT", sql);
        Assert.Equal(2, sql.Split("SEEDING_MODE = AUTOMATIC").Length - 1);
        // Automatic failover asked for on n2, but NONE has no cluster to fail over with.
        Assert.DoesNotContain("FAILOVER_MODE = AUTOMATIC", sql);
    }

    [Fact]
    public void Automatic_failover_only_for_a_synchronous_replica_under_wsfc()
    {
        var wsfc = Choices with
        {
            ClusterType = "WSFC",
            Replicas = [new ReplicaChoice("n1", "n1", true, true), new ReplicaChoice("n2", "n2", false, true)]
        };

        var steps = NewAvailabilityGroupPlan.Plan(wsfc, [Instance("n1"), Instance("n2")], false, Secret);

        Assert.Equal(1, steps[0].Sql.Split("FAILOVER_MODE = AUTOMATIC").Length - 1);
        Assert.Equal("ALTER AVAILABILITY GROUP [ag2] JOIN;", steps[1].Sql);   // WSFC: the plain form 2016 knows too
        Assert.True(steps[1].Retries);
        Assert.False(steps[2].Retries);
    }

    [Fact]
    public void Read_scale_joins_with_its_cluster_type()
    {
        var steps = NewAvailabilityGroupPlan.Plan(Choices, [Instance("n1"), Instance("n2")], false, Secret);

        Assert.Equal("ALTER AVAILABILITY GROUP [ag2] JOIN WITH (CLUSTER_TYPE = NONE);", steps[1].Sql);
    }

    [Fact]
    public void Standard_edition_creates_a_basic_group()
    {
        var sql = NewAvailabilityGroupPlan.Plan(Choices with { Databases = ["Sales"] },
            [Instance("n1", edition: 2), Instance("n2", edition: 2)], false, Secret)[0].Sql;

        Assert.Contains("BASIC", sql);
        Assert.DoesNotContain("DB_FAILOVER", sql);
    }

    // ── The runner's substitution ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_captured_certificate_is_substituted_as_a_binary_literal()
    {
        var captured = new Dictionary<string, string> { ["cert:n2"] = AgStepRunner.Literal(new byte[] { 0x30, 0x82, 0x01 }) ?? "" };

        Assert.Equal("FROM BINARY = 0x308201;", AgStepRunner.Substitute("FROM BINARY = $(cert:n2);", captured));
    }

    [Fact]
    public void An_uncaptured_token_is_an_error_not_an_empty_string()
    {
        Assert.Throws<InvalidOperationException>(() => AgStepRunner.Substitute("FROM BINARY = $(cert:n9);", new Dictionary<string, string>()));
    }

    [Fact]
    public void Only_certificate_tokens_are_substituted()
    {
        // A database may legitimately be called "$(x)"; the failover plan runs through the same runner.
        Assert.Equal("ALTER DATABASE [$(x)] SET HADR RESUME;", AgStepRunner.Substitute("ALTER DATABASE [$(x)] SET HADR RESUME;", new Dictionary<string, string>()));
    }

    [Fact]
    public void Only_a_non_empty_binary_is_a_capturable_value()
    {
        Assert.Null(AgStepRunner.Literal(null));
        Assert.Null(AgStepRunner.Literal(Array.Empty<byte>()));
        Assert.Null(AgStepRunner.Literal("0x30"));
    }
}
