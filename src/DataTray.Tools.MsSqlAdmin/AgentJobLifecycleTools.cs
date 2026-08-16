namespace DataTray.Tools.MsSqlAdmin;

/// <summary>
/// Creating and dropping Agent jobs (SE-235). These are tree actions rather than pages in the job dialog —
/// you make a job before it has properties, and once it is gone there is nothing left to open — so they are
/// tools like Start/Stop, hanging off the Agent Jobs folder and off a job.
/// </summary>
public sealed class NewAgentJobTool : IToolPlugin
{
    public const string NameKey = "name";
    public const string DescriptionKey_ = "description";
    public const string EnabledKey = "enabled";

    public string Id => "mssql-agent-job-new";

    public string Title => "New Job…";

    public string? TitleKey => "agentjob.new.title";

    public string? Description =>
        "Creates an empty job on this server and targets it at the local server, which a job needs before it "
        + "can run at all. Add its steps and schedules from the job's Properties.";

    public string? DescriptionKey => "agentjob.new.description";

    // The Agent Jobs folder has its own node kind so this lands there and nowhere else.
    public ToolTarget Target { get; } = new(ProviderIds: ["sqlserver"], NodeKinds: [DbNodeKind.AgentJobFolder]);

    /// <summary>New Job… is what the Jobs folder is for, so it belongs on its context menu rather than under
    /// Tools ▸ (SE-261).</summary>
    public bool IsNodeAction => true;

    public IReadOnlyList<ToolField> Fields { get; } =
    [
        new(NameKey, "Name", Required: true, LabelKey: "agentjob.new.field.name"),
        new(DescriptionKey_, "Description", LabelKey: "agentjob.new.field.description"),
        new(EnabledKey, "Enabled", ToolFieldType.Bool, Default: "true", LabelKey: "agentjob.new.field.enabled")
    ];

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var name = inputs.GetValueOrDefault(NameKey);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(context.Localizer["agentjob.new.error.noName"]);
        }

        var literal = name.Replace("'", "''");
        var description = (inputs.GetValueOrDefault(DescriptionKey_) ?? string.Empty).Replace("'", "''");
        var enabled = inputs.GetValueOrDefault(EnabledKey) != "false";

        // sp_add_job alone leaves a job no server will ever run; sp_add_jobserver is what makes it real.
        var sql = $"EXEC msdb.dbo.sp_add_job @job_name = N'{literal}', @enabled = {(enabled ? 1 : 0)}"
                  + $", @description = N'{description}';\n"
                  + $"EXEC msdb.dbo.sp_add_jobserver @job_name = N'{literal}'";

        progress.Report(new ToolProgress(context.Localizer.Get("agentjob.progress.running", sql)));
        await context.Provider.ExecuteDdlAsync(context.Profile, sql, ct);
        progress.Report(new ToolProgress(context.Localizer.Get("agentjob.new.progress.complete", name), 1.0));
    }
}

/// <summary>Drops a job, its steps, its schedule attachments and its history in one go.</summary>
public sealed class DeleteAgentJobTool : IToolPlugin
{
    public string Id => "mssql-agent-job-delete";

    public string Title => "Delete Job…";

    public string? TitleKey => "agentjob.delete.title";

    public string? Description =>
        "Deletes the job together with its steps, its history and its links to any schedules. A schedule "
        + "shared with another job survives; one only this job used goes with it.";

    public string? DescriptionKey => "agentjob.delete.description";

    public ToolTarget Target { get; } = new(ProviderIds: ["sqlserver"], NodeKinds: [DbNodeKind.AgentJob]);

    /// <summary>Delete is one of the job's own verbs, so it renders on the job's context menu next to
    /// Start/Stop rather than under Tools ▸ (SE-261).</summary>
    public bool IsNodeAction => true;

    public IReadOnlyList<ToolField> Fields { get; } = [];

    // Unlike the other job actions this one cannot be undone by doing the opposite.
    public bool IsDestructive => true;

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var job = context.Node?.Name
            ?? throw new InvalidOperationException(context.Localizer["agentjob.error.noJob"]);

        var sql = $"EXEC msdb.dbo.sp_delete_job @job_name = N'{job.Replace("'", "''")}'";
        progress.Report(new ToolProgress(context.Localizer.Get("agentjob.progress.running", sql)));
        await context.Provider.ExecuteDdlAsync(context.Profile, sql, ct);
        progress.Report(new ToolProgress(context.Localizer.Get("agentjob.delete.progress.complete", job), 1.0));
    }
}
