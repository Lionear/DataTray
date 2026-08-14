namespace DataTray.Core.Update;

public enum UpdateStatus
{
    /// <summary>The running build is the newest the channel offers.</summary>
    UpToDate,

    /// <summary>A newer build is available (see <see cref="UpdateCheckResult.Build"/>).</summary>
    Available,

    /// <summary>The check couldn't complete (offline, fetch failure). Treated as silent.</summary>
    Failed
}

/// <summary>
/// Outcome of <see cref="IUpdateService.CheckAsync"/>. When <see cref="Status"/> is
/// <see cref="UpdateStatus.Available"/>, <see cref="Build"/> describes what is on offer.
/// <para>
/// There is no longer a separate "nothing for this platform" case: a Velopack feed is per RID, so a
/// feed that answers at all answers with a package this install can actually take. The old
/// manifest carried every platform's assets and could legitimately have none for the running one.
/// </para>
/// </summary>
public sealed record UpdateCheckResult(UpdateStatus Status, OfferedBuild? Build = null)
{
    public static readonly UpdateCheckResult UpToDate = new(UpdateStatus.UpToDate);
    public static readonly UpdateCheckResult Failed = new(UpdateStatus.Failed);

    public static UpdateCheckResult Available(OfferedBuild build) =>
        new(UpdateStatus.Available, build);

    public bool IsAvailable => Status == UpdateStatus.Available;
}
