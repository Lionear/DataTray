namespace DataTray.Providers.MsSql;

/// <summary>
/// Turns a schedule's msdb encoding into the sentence SSMS shows under a job's schedule list. Agent can
/// produce one itself (<c>sp_help_jobschedule @include_description = 1</c>) but renders the time as the raw
/// packed int — "Every day at 20000" — so this does it properly. Pure and public, because the encoding is
/// six interacting integer fields and getting it wrong is both easy and invisible.
/// </summary>
public static class AgentScheduleText
{
    // sysschedules.freq_type
    public const int Once = 1;
    public const int Daily = 4;
    public const int Weekly = 8;
    public const int Monthly = 16;
    public const int MonthlyRelative = 32;
    public const int OnAgentStart = 64;
    public const int OnIdle = 128;

    // freq_interval for a weekly schedule is a bitmask over these.
    private static readonly (int Bit, string Name)[] Days =
    [
        (1, "Sun"), (2, "Mon"), (4, "Tue"), (8, "Wed"), (16, "Thu"), (32, "Fri"), (64, "Sat")
    ];

    // freq_interval for a monthly-relative schedule: a weekday, or one of three grouped meanings.
    private static readonly Dictionary<int, string> RelativeDays = new()
    {
        [1] = "Sunday", [2] = "Monday", [3] = "Tuesday", [4] = "Wednesday", [5] = "Thursday",
        [6] = "Friday", [7] = "Saturday", [8] = "day", [9] = "weekday", [10] = "weekend day"
    };

    private static readonly Dictionary<int, string> Ordinals = new()
    {
        [1] = "first", [2] = "second", [4] = "third", [8] = "fourth", [16] = "last"
    };

    /// <summary>
    /// The whole schedule as one sentence. <paramref name="startTime"/>/<paramref name="endTime"/> are
    /// <c>hhmmss</c> packed ints and <paramref name="startDate"/> is <c>yyyymmdd</c>, all Agent's own encoding.
    /// </summary>
    public static string Describe(
        int freqType, int freqInterval, int subdayType, int subdayInterval,
        int relativeInterval, int recurrenceFactor, int startDate, int startTime, int endTime)
    {
        var when = freqType switch
        {
            OnAgentStart => "When SQL Server Agent starts",
            OnIdle => "When the CPUs become idle",
            Once => $"Once on {Date(startDate)}",
            Daily => Every(recurrenceFactor, "day"),
            Weekly => $"{Every(recurrenceFactor, "week")} on {WeekDays(freqInterval)}",
            Monthly => $"Day {freqInterval} of {Every(recurrenceFactor, "month").ToLowerInvariant()}",
            MonthlyRelative =>
                $"The {Ordinals.GetValueOrDefault(relativeInterval, "first")} " +
                $"{RelativeDays.GetValueOrDefault(freqInterval, "day")} of " +
                $"{Every(recurrenceFactor, "month").ToLowerInvariant()}",
            _ => "Unscheduled"
        };

        // The automatic types carry no time of day at all; everything else does.
        return freqType is OnAgentStart or OnIdle ? when : $"{when}, {TimeOfDay(subdayType, subdayInterval, startTime, endTime)}";
    }

    /// <summary>The time-of-day half: a single moment, or a repeat within a window.</summary>
    private static string TimeOfDay(int subdayType, int subdayInterval, int startTime, int endTime)
    {
        var unit = subdayType switch
        {
            2 => subdayInterval == 1 ? "second" : "seconds",
            4 => subdayInterval == 1 ? "minute" : "minutes",
            8 => subdayInterval == 1 ? "hour" : "hours",
            _ => null
        };

        return unit is null
            ? $"at {Time(startTime)}"
            : $"every {subdayInterval} {unit} between {Time(startTime)} and {Time(endTime)}";
    }

    private static string Every(int factor, string unit) =>
        factor <= 1 ? $"Every {unit}" : $"Every {factor} {unit}s";

    private static string WeekDays(int mask)
    {
        var names = Days.Where(d => (mask & d.Bit) != 0).Select(d => d.Name).ToList();
        return names.Count == 0 ? "no day" : string.Join(", ", names);
    }

    private static string Date(int yyyymmdd) => yyyymmdd == 0
        ? "an unset date"
        : $"{yyyymmdd / 10000:D4}-{yyyymmdd / 100 % 100:D2}-{yyyymmdd % 100:D2}";

    private static string Time(int hhmmss) =>
        $"{hhmmss / 10000:D2}:{hhmmss / 100 % 100:D2}:{hhmmss % 100:D2}";
}
