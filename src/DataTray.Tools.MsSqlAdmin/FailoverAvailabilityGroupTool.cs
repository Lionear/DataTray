using Avalonia.Controls;
using DataTray.Sdk;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// Fail Over… on an availability group (SE-247). Always On is multi-instance by nature: the promoting
/// statement runs on the instance that hosts the target, so the user maps the replicas involved to saved
/// connections, reviews the exact plan (<see cref="AvailabilityGroupFailover"/>), and — for anything but a
/// planned, loss-free failover — types the group name. <see cref="ExecuteAsync"/> then re-reads the state,
/// rebuilds the plan and refuses to run if it is not the script the user reviewed.
/// </summary>
/// <remarks>
/// A tool, so it is reachable only from the tree in the desktop app: the MCP surface exposes no tools at all,
/// and its <c>run_query</c> classifies every statement here as DDL, which no AI access mode short of a
/// transient Sandbox connection allows.
/// </remarks>
public sealed class FailoverAvailabilityGroupTool : IToolPlugin, ICustomToolUi
{
    public const string TargetKey = "target";
    public const string PrimaryConnectionKey = "primaryConnection";
    public const string TargetConnectionKey = "targetConnection";
    public const string ConfirmKey = "confirm";
    public const string ReviewedKey = "reviewed";

    public string Id => "mssql-ag-failover";

    public string Title => "Fail Over…";

    public string? TitleKey => "agfailover.title";

    public string DialogTitle => "Fail Over Availability Group";

    public string? DialogTitleKey => "agfailover.dialogTitle";

    public ToolTarget Target { get; } = new(ProviderIds: ["sqlserver"], NodeKinds: [DbNodeKind.AvailabilityGroup]);

    /// <summary>Failing over is the group's own verb, next to its dashboard (SE-261).</summary>
    public bool IsNodeAction => true;

    public IReadOnlyList<ToolField> Fields { get; } = [];

    public bool IsDestructive => true;

    public Control CreateView(IToolUiContext context) => new FailoverAvailabilityGroupView(context);

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var group = context.Node?.Name ?? throw new InvalidOperationException("No availability group selected.");
        var target = inputs.GetValueOrDefault(TargetKey)
            ?? throw new InvalidOperationException("Choose the replica to fail over to.");

        // Re-read and re-plan against fresh state: the plan the user reviewed may be minutes old, and a
        // target that stopped being failover-ready turns a planned failover into a forced one.
        var launch = new ReplicaConnection(context.Provider, context.Profile);
        var session = await FailoverSession.ReadAsync(launch, group, id => Open(context.Host, id), inputs.GetValueOrDefault(PrimaryConnectionKey), inputs.GetValueOrDefault(TargetConnectionKey), target, ct);
        var plan = AvailabilityGroupFailover.Plan(session.Topology, target);

        if (plan.Kind == FailoverKind.Refused)
        {
            throw new InvalidOperationException(plan.RefusalReason);
        }

        if (plan.Script() != inputs.GetValueOrDefault(ReviewedKey))
        {
            throw new InvalidOperationException("The group's state changed since the plan was shown, and the plan with it. Nothing was run — review the new plan and confirm again.");
        }

        if (plan.NeedsTypedConfirmation && inputs.GetValueOrDefault(ConfirmKey) != group)
        {
            throw new InvalidOperationException($"Type the group name ({group}) to confirm. Nothing was run.");
        }

        // Every connection must be in hand before the first statement: a read-scale plan that stops after
        // OFFLINE because the target was never mapped leaves the group down.
        await AgStepRunner.RunAsync(plan.Steps, session.ConnectionFor, progress, ct);

        progress.Report(new ToolProgress($"{target} is now the primary replica of {group}.", 1.0));
    }

    private static ReplicaConnection? Open(IToolHost host, string connectionId) =>
        host.OpenConnection(connectionId) is { } c ? new ReplicaConnection(c.Provider, c.Profile) : null;
}

/// <summary>A connection to one replica's instance.</summary>
internal sealed record ReplicaConnection(IDbProvider Provider, ConnectionProfile Profile)
{
    public Task<QueryResult> QueryAsync(string sql, CancellationToken ct) => Provider.ExecuteQueryAsync(Profile, sql, ct);
}

/// <summary>
/// The replicas involved in one failover, each with the connection it is reached through, and the topology
/// read over them. Shared by the view (to show the plan) and <c>ExecuteAsync</c> (to re-check it), so both
/// read the same state the same way.
/// </summary>
internal sealed class FailoverSession
{
    private readonly Dictionary<string, ReplicaConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

    private FailoverSession(AgTopology topology, string local, IReadOnlyList<string> problems)
    {
        Topology = topology;
        LocalReplica = local;
        Problems = problems;
    }

    public AgTopology Topology { get; private set; }

    /// <summary>The replica the launch connection is.</summary>
    public string LocalReplica { get; }

    /// <summary>Why a picked connection was not used — shown in the view, never silently dropped.</summary>
    public IReadOnlyList<string> Problems { get; }

    public ReplicaConnection? ConnectionFor(string replica) => _connections.GetValueOrDefault(replica);

    /// <summary>
    /// Read the group through the launch connection, attach the picked connections for the primary and the
    /// target (each only after <c>SERVERPROPERTY('ServerName')</c> proves it is that replica), and re-read the
    /// topology from the primary when it is reachable — a secondary sees only its own rows in the state DMVs,
    /// and a forced failover's loss estimate needs the primary's last commit times.
    /// </summary>
    public static async Task<FailoverSession> ReadAsync(
        ReplicaConnection launch,
        string group,
        Func<string, ReplicaConnection?> open,
        string? primaryConnectionId,
        string? targetConnectionId,
        string? target,
        CancellationToken ct)
    {
        var topology = await ReadTopologyAsync(launch, group, ct);
        var local = AvailabilityGroupQueries.LocalReplica((await launch.QueryAsync(AvailabilityGroupQueries.Replicas(group), ct)).Rows)
            ?? throw new InvalidOperationException($"This connection hosts no replica of {group}.");

        var problems = new List<string>();
        var session = new FailoverSession(topology, local, problems);
        session._connections[local] = launch;

        async Task Attach(string? replica, string? connectionId)
        {
            if (replica is null || session._connections.ContainsKey(replica) || string.IsNullOrEmpty(connectionId))
            {
                return;
            }

            if (open(connectionId) is not { } connection)
            {
                problems.Add($"The connection picked for {replica} no longer exists.");
                return;
            }

            try
            {
                var name = (await connection.QueryAsync(AvailabilityGroupQueries.ServerName, ct)).Rows.FirstOrDefault()?[0] as string;
                if (!string.Equals(name, replica, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"The connection picked for {replica} reaches {name ?? "an unnamed instance"}, not {replica}.");
                    return;
                }

                session._connections[replica] = connection;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"Could not reach {replica}: {ex.Message}");
            }
        }

        await Attach(topology.Primary, primaryConnectionId);
        await Attach(target, targetConnectionId);

        if (topology.Primary is { } primary && !string.Equals(primary, local, StringComparison.OrdinalIgnoreCase)
            && session.ConnectionFor(primary) is { } primaryConnection)
        {
            try
            {
                session.Topology = await ReadTopologyAsync(primaryConnection, group, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"Could not read the group's state from the primary ({ex.Message}); showing what {local} sees.");
            }
        }

        return session;
    }

    private static async Task<AgTopology> ReadTopologyAsync(ReplicaConnection c, string group, CancellationToken ct) =>
        AvailabilityGroupQueries.Parse(group,
            (await c.QueryAsync(AvailabilityGroupQueries.Group(group), ct)).Rows,
            (await c.QueryAsync(AvailabilityGroupQueries.Replicas(group), ct)).Rows,
            (await c.QueryAsync(AvailabilityGroupQueries.Databases(group), ct)).Rows,
            (await c.QueryAsync(AvailabilityGroupQueries.Listener(group), ct)).Rows);
}
