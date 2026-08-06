namespace DataTray.Providers.MsSql;

/// <summary>
/// Decodes what <c>msdb.dbo.sysjobservers</c> records about a job's last run into the labels the tree shows.
/// Public and pure so the integer date/time encoding — which is not a datetime but two packed ints — is
/// covered by a test instead of being eyeballed against a live Agent.
/// </summary>
public static class AgentJobStatus
{
    /// <summary>
    /// The badge for a job's last outcome, or null when there is nothing worth flagging. A succeeded job gets
    /// no badge (that would put a label on every healthy job). Neither does a job that has never run: its
    /// <c>last_run_outcome</c> is not a result at all — 5 ("unknown") on current builds, 0 elsewhere, and 0 is
    /// the same value a genuine failure carries. <c>last_run_date</c> of 0 is the reliable tell, so that is
    /// what decides, rather than trusting the outcome to be absent.
    /// </summary>
    public static string? Badge(byte outcome, int lastRunDate) => lastRunDate == 0
        ? null
        : outcome switch
        {
            0 => "failed",
            2 => "retry",
            3 => "canceled",
            _ => null
        };

    /// <summary>
    /// The tooltip for a job's last run. <paramref name="date"/> is <c>yyyymmdd</c> and <paramref name="time"/>
    /// is <c>hhmmss</c>, both as plain ints (Agent's own encoding), with 0 for "never ran".
    /// </summary>
    public static string? LastRun(int date, int time) => date == 0
        ? null
        : $"Last run {date / 10000:D4}-{date / 100 % 100:D2}-{date % 100:D2} " +
          $"{time / 10000:D2}:{time / 100 % 100:D2}:{time % 100:D2}";
}
