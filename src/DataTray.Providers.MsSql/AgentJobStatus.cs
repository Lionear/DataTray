namespace DataTray.Providers.MsSql;

/// <summary>
/// Decodes what msdb records about a job's runs into the labels the tree and the job properties show.
/// Public and pure so the integer encodings — none of these are datetimes, they are packed ints — are
/// covered by tests instead of being eyeballed against a live Agent.
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
    /// A point in time as Agent stores it: <paramref name="date"/> is <c>yyyymmdd</c> and
    /// <paramref name="time"/> is <c>hhmmss</c>, both plain ints. Null when the date is 0, which is Agent's
    /// way of saying it never happened.
    /// </summary>
    public static string? Timestamp(int date, int time) => date == 0
        ? null
        : $"{date / 10000:D4}-{date / 100 % 100:D2}-{date % 100:D2} " +
          $"{time / 10000:D2}:{time / 100 % 100:D2}:{time % 100:D2}";

    /// <summary>The tooltip for a job's last run — <see cref="Timestamp"/> with the label the tree wants.</summary>
    public static string? LastRun(int date, int time) =>
        Timestamp(date, time) is { } stamp ? $"Last run {stamp}" : null;

    /// <summary>
    /// The name of a run outcome. The same small set is spelled two ways in msdb — <c>last_run_outcome</c>
    /// (tinyint) and <c>sysjobhistory.run_status</c> (int) — so this takes the wider one and both callers fit.
    /// </summary>
    public static string OutcomeName(int outcome) => outcome switch
    {
        0 => "failed",
        1 => "succeeded",
        2 => "retry",
        3 => "canceled",
        4 => "in progress",
        _ => "unknown"
    };

    /// <summary>
    /// A run's duration, stored as <c>HHMMSS</c> packed into an int — so 3 is three seconds and 10203 is an
    /// hour, two minutes and three seconds, neither of which is the number it looks like.
    /// </summary>
    public static string Duration(int hhmmss)
    {
        var (h, m, s) = (hhmmss / 10000, hhmmss / 100 % 100, hhmmss % 100);
        return h > 0 ? $"{h}h {m:D2}m {s:D2}s" : m > 0 ? $"{m}m {s:D2}s" : $"{s}s";
    }
}
