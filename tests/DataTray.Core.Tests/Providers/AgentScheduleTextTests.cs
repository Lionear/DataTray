using DataTray.Providers.MsSql;

namespace DataTray.Core.Tests.Providers;

/// <summary>
/// A schedule is six interacting integer fields in msdb, and a description built from them is wrong in ways
/// nobody notices until a job runs at the wrong time. The cases below are the ones the editor can produce.
/// </summary>
public class AgentScheduleTextTests
{
    // The schedule created live to check sp_add_schedule: daily at 02:00. Agent's own description renders
    // this as "Every day at 20000", which is the reason this class exists.
    [Fact]
    public void Daily_once_a_day_reads_as_a_time_not_a_packed_int() =>
        Assert.Equal("Every day, at 02:00:00", Describe(AgentScheduleText.Daily, 1, 1, 0, 0, 0, 20000, 235959));

    [Fact]
    public void Daily_every_few_days_counts_them() =>
        Assert.Equal("Every 3 days, at 02:00:00", Describe(AgentScheduleText.Daily, 1, 1, 0, 0, 3, 20000, 235959));

    // freq_interval is a weekday bitmask here: 2 = Mon, 8 = Wed, 32 = Fri.
    [Fact]
    public void Weekly_lists_the_days_from_the_bitmask() =>
        Assert.Equal("Every 2 weeks on Mon, Wed, Fri, at 23:30:00",
            Describe(AgentScheduleText.Weekly, 2 + 8 + 32, 1, 0, 0, 2, 233000, 235959));

    [Fact]
    public void Monthly_names_the_day_of_the_month() =>
        Assert.Equal("Day 15 of every month, at 06:00:00",
            Describe(AgentScheduleText.Monthly, 15, 1, 0, 0, 1, 60000, 235959));

    // relative_interval 2 = second, freq_interval 6 = Friday.
    [Fact]
    public void Monthly_relative_reads_as_a_sentence() =>
        Assert.Equal("The second Friday of every 3 months, at 01:00:00",
            Describe(AgentScheduleText.MonthlyRelative, 6, 1, 0, 2, 3, 10000, 235959));

    // subday_type 4 = minutes, so this repeats inside a window rather than firing once.
    [Fact]
    public void A_subday_interval_becomes_a_window() =>
        Assert.Equal("Every day, every 30 minutes between 08:00:00 and 17:00:00",
            Describe(AgentScheduleText.Daily, 1, 4, 30, 0, 1, 80000, 170000));

    [Fact]
    public void A_single_unit_is_not_pluralised() =>
        Assert.Equal("Every day, every 1 hour between 08:00:00 and 17:00:00",
            Describe(AgentScheduleText.Daily, 1, 8, 1, 0, 1, 80000, 170000));

    // These two carry no time of day at all — appending one would be a lie.
    [Theory]
    [InlineData(AgentScheduleText.OnAgentStart, "When SQL Server Agent starts")]
    [InlineData(AgentScheduleText.OnIdle, "When the CPUs become idle")]
    public void The_automatic_types_carry_no_time(int freqType, string expected) =>
        Assert.Equal(expected, Describe(freqType, 0, 0, 0, 0, 0, 0, 0));

    [Fact]
    public void One_time_names_the_date_it_runs() =>
        Assert.Equal("Once on 2026-08-05, at 02:00:00",
            Describe(AgentScheduleText.Once, 0, 1, 0, 0, 0, 20000, 235959, startDate: 20260805));

    private static string Describe(
        int freqType, int freqInterval, int subdayType, int subdayInterval,
        int relativeInterval, int recurrenceFactor, int startTime, int endTime, int startDate = 20260101) =>
        AgentScheduleText.Describe(freqType, freqInterval, subdayType, subdayInterval,
            relativeInterval, recurrenceFactor, startDate, startTime, endTime);
}
