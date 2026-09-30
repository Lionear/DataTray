using System.Text.Json;
using Avalonia.Controls;

namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// New Availability Group… on the Availability Groups folder (SE-247). The instance the tool was opened on
/// becomes the primary; the user picks saved connections as secondaries. A pre-flight over all of them decides
/// what the form offers at all (<see cref="NewAvailabilityGroupPlan.Check"/>), the plan is shown in full, and
/// <see cref="ExecuteAsync"/> re-reads every instance and refuses unless it rebuilds exactly the reviewed script.
/// Like the failover, it is a tool: nothing on the MCP surface reaches it.
/// </summary>
public sealed class NewAvailabilityGroupTool : IToolPlugin, ICustomToolUi
{
    public const string SecondariesKey = "secondaries";
    public const string ChoicesKey = "choices";
    public const string SecretsKey = "secrets";
    public const string ReviewedKey = "reviewed";

    public string Id => "mssql-ag-new";

    public string Title => "New Availability Group…";

    public string? TitleKey => "agnew.title";

    public string DialogTitle => "New Availability Group";

    public string? DialogTitleKey => "agnew.dialogTitle";

    public ToolTarget Target { get; } = new(ProviderIds: ["sqlserver"], NodeKinds: [DbNodeKind.AvailabilityGroupFolder]);

    /// <summary>What the folder is for, like New Job… on Agent Jobs (SE-261).</summary>
    public bool IsNodeAction => true;

    public IReadOnlyList<ToolField> Fields { get; } = [];

    /// <summary>It creates endpoints, certificates and logins on every instance it touches — the host's
    /// confirmation comes after the user has read the plan.</summary>
    public bool IsDestructive => true;

    public Control CreateView(IToolUiContext context) => new NewAvailabilityGroupView(context);

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var session = await NewGroupSession.ReadAsync(
            new ReplicaConnection(context.Provider, context.Profile),
            SplitIds(inputs.GetValueOrDefault(SecondariesKey)),
            id => context.Host.OpenConnection(id) is { } c ? new ReplicaConnection(c.Provider, c.Profile) : null,
            ct);

        if (session.Problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", session.Problems) + " Nothing was run.");
        }

        if (!session.Preflight.CanProceed)
        {
            throw new InvalidOperationException("The pre-flight no longer passes — " + string.Join(" ", session.Preflight.Checks.Where(c => c.Level == CheckLevel.Blocking).Select(c => $"{c.Subject}: {c.Message}")) + " Nothing was run.");
        }

        var choices = JsonSerializer.Deserialize<NewGroupChoices>(inputs.GetValueOrDefault(ChoicesKey) ?? "null")
            ?? throw new InvalidOperationException("No choices were made.");
        if (NewAvailabilityGroupPlan.Validate(choices, session.Preflight) is { } invalid)
        {
            throw new InvalidOperationException(invalid);
        }

        var secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(inputs.GetValueOrDefault(SecretsKey) ?? "{}") ?? [];
        var steps = NewAvailabilityGroupPlan.Plan(choices, session.Instances, session.Preflight.CreateEndpoints,
            key => secrets.TryGetValue(key, out var v) ? v : throw new InvalidOperationException($"Missing generated secret {key}."));

        if (AgStepRunner.Script(steps) != inputs.GetValueOrDefault(ReviewedKey))
        {
            throw new InvalidOperationException("The instances changed since the plan was shown, and the plan with it. Nothing was run — review the new plan and confirm again.");
        }

        await AgStepRunner.RunAsync(steps, session.ConnectionFor, progress, ct);
        progress.Report(new ToolProgress($"Created {choices.Group}. Its databases are seeded to the secondaries in the background — follow them on the group's dashboard.", 1.0));
    }

    internal static IReadOnlyList<string> SplitIds(string? value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// The instances a new group would span — the launch connection first, as its primary — with the facts
/// each reports about itself and the pre-flight over all of them. Replica names come from the instances
/// themselves (<c>SERVERPROPERTY('ServerName')</c>), so a connection cannot be filed under the wrong one.
/// </summary>
internal sealed class NewGroupSession
{
    private readonly Dictionary<string, ReplicaConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

    private NewGroupSession(IReadOnlyList<InstanceFacts> instances, IReadOnlyList<DatabaseFacts> databases, IReadOnlyList<string> problems)
    {
        Instances = instances;
        Databases = databases;
        Problems = problems;
        Preflight = NewAvailabilityGroupPlan.Check(instances, databases);
    }

    public IReadOnlyList<InstanceFacts> Instances { get; }

    public IReadOnlyList<DatabaseFacts> Databases { get; }

    public IReadOnlyList<string> Problems { get; }

    public Preflight Preflight { get; }

    public ReplicaConnection? ConnectionFor(string replica) => _connections.GetValueOrDefault(replica);

    public static async Task<NewGroupSession> ReadAsync(
        ReplicaConnection launch,
        IReadOnlyList<string> secondaryIds,
        Func<string, ReplicaConnection?> open,
        CancellationToken ct)
    {
        var problems = new List<string>();
        var read = new List<(InstanceFacts Facts, ReplicaConnection Connection)> { (await FactsAsync(launch, ct), launch) };

        foreach (var id in secondaryIds)
        {
            if (open(id) is not { } connection)
            {
                problems.Add("A picked connection no longer exists.");
                continue;
            }

            try
            {
                read.Add((await FactsAsync(connection, ct), connection));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"Could not read {connection.Profile.Name}: {ex.Message}");
            }
        }

        var databases = (await launch.QueryAsync(AvailabilityGroupQueries.CandidateDatabases, ct)).Rows
            .Select(AvailabilityGroupQueries.ParseDatabase).ToList();
        var session = new NewGroupSession(read.Select(r => r.Facts).ToList(), databases, problems);
        foreach (var (facts, connection) in read)
        {
            session._connections.TryAdd(facts.Name, connection);
        }

        return session;
    }

    private static async Task<InstanceFacts> FactsAsync(ReplicaConnection c, CancellationToken ct)
    {
        var rows = (await c.QueryAsync(AvailabilityGroupQueries.Instance, ct)).Rows;
        var names = (await c.QueryAsync(AvailabilityGroupQueries.DatabaseNames, ct)).Rows;
        return AvailabilityGroupQueries.ParseInstance(rows[0], names);
    }
}
