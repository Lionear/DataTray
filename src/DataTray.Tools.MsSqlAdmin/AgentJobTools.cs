namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// SSMS' SQL Server Agent job actions (right-click a job ▸ Start/Stop/Enable/Disable). Route A with no
/// fields: there is nothing to ask, so the host's dialog is the description, an Execute button and the
/// progress log — which is what you want here, since <c>sp_start_job</c> returns immediately and any Agent
/// error (job already running, no permission on msdb) surfaces in that log. The job editor — steps,
/// schedules, notifications — is deliberately not here; that is SSMS' five-page dialog and this covers the
/// day-to-day.
/// </summary>
public abstract class AgentJobTool : IToolPlugin
{
    public abstract string Id { get; }

    public abstract string Title { get; }

    public abstract string? TitleKey { get; }

    public abstract string? Description { get; }

    public abstract string? DescriptionKey { get; }

    // Offered on an Agent job node only — the reason AgentJob is its own node kind rather than Object.
    public ToolTarget Target { get; } = new(ProviderIds: ["sqlserver"], NodeKinds: [DbNodeKind.AgentJob]);

    /// <summary>These are the job's own verbs, not extras offered on it, so they render straight on the
    /// context menu the way SSMS has them rather than under Tools ▸ (SE-261). <c>MenuPath</c> is left at its
    /// default because a node action ignores it.</summary>
    public bool IsNodeAction => true;

    public IReadOnlyList<ToolField> Fields { get; } = [];

    // SSMS confirms none of these — starting or disabling a job is undone by the opposite action.
    public bool IsDestructive => false;

    /// <summary>The msdb procedure call for the job, whose name arrives already escaped for a SQL literal.</summary>
    protected abstract string BuildSql(string jobLiteral);

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var job = context.Node?.Name
            ?? throw new InvalidOperationException(context.Localizer["agentjob.error.noJob"]);

        var sql = BuildSql(job.Replace("'", "''"));
        progress.Report(new ToolProgress(context.Localizer.Get("agentjob.progress.running", sql)));
        await context.Provider.ExecuteDdlAsync(context.Profile, sql, ct);
        progress.Report(new ToolProgress(context.Localizer.Get("agentjob.progress.complete", job), 1.0));
    }
}

public sealed class StartAgentJobTool : AgentJobTool
{
    public override string Id => "mssql-agent-job-start";

    public override string Title => "Start Job";

    public override string? TitleKey => "agentjob.start.title";

    public override string? Description =>
        "Starts the job now, at its first step. Agent runs it in the background — the outcome lands in the "
        + "job's history, not here.";

    public override string? DescriptionKey => "agentjob.start.description";

    // No @step_name: SSMS' "Start Job at Step…" is a separate command, and starting at step 1 is the default.
    protected override string BuildSql(string jobLiteral) =>
        $"EXEC msdb.dbo.sp_start_job @job_name = N'{jobLiteral}'";
}

public sealed class StopAgentJobTool : AgentJobTool
{
    public override string Id => "mssql-agent-job-stop";

    public override string Title => "Stop Job";

    public override string? TitleKey => "agentjob.stop.title";

    public override string? Description =>
        "Asks Agent to stop the running job. A job that is not running reports an error rather than stopping "
        + "silently.";

    public override string? DescriptionKey => "agentjob.stop.description";

    protected override string BuildSql(string jobLiteral) =>
        $"EXEC msdb.dbo.sp_stop_job @job_name = N'{jobLiteral}'";
}

public sealed class EnableAgentJobTool : AgentJobTool
{
    public override string Id => "mssql-agent-job-enable";

    public override string Title => "Enable Job";

    public override string? TitleKey => "agentjob.enable.title";

    public override string? Description =>
        "Lets the job's schedules fire again. Does not start it now.";

    public override string? DescriptionKey => "agentjob.enable.description";

    protected override string BuildSql(string jobLiteral) =>
        $"EXEC msdb.dbo.sp_update_job @job_name = N'{jobLiteral}', @enabled = 1";
}

public sealed class DisableAgentJobTool : AgentJobTool
{
    public override string Id => "mssql-agent-job-disable";

    public override string Title => "Disable Job";

    public override string? TitleKey => "agentjob.disable.title";

    public override string? Description =>
        "Stops the job's schedules from firing. A run already in progress keeps going.";

    public override string? DescriptionKey => "agentjob.disable.description";

    protected override string BuildSql(string jobLiteral) =>
        $"EXEC msdb.dbo.sp_update_job @job_name = N'{jobLiteral}', @enabled = 0";
}
