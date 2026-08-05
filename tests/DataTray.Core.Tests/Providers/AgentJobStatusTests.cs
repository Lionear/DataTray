using DataTray.Providers.MsSql;

namespace DataTray.Core.Tests.Providers;

/// <summary>
/// Agent stores a job's last run as two packed ints rather than a datetime, and gives a job that never ran
/// an outcome of 0 — the same value as "failed". Both are easy to get subtly wrong and invisible without a
/// live Agent, so they are pinned here.
/// </summary>
public class AgentJobStatusTests
{
    [Theory]
    [InlineData(0, "failed")]
    [InlineData(2, "retry")]
    [InlineData(3, "canceled")]
    public void Badge_flags_a_run_that_did_not_succeed(byte outcome, string expected) =>
        Assert.Equal(expected, AgentJobStatus.Badge(outcome, 20260805));

    [Fact]
    public void Badge_is_empty_for_a_succeeded_run() =>
        Assert.Null(AgentJobStatus.Badge(1, 20260805));

    // A job that never ran carries 5 ("unknown") on current builds and 0 elsewhere — and 0 on its own reads
    // as "failed". The zero date is what has to keep both unlabelled.
    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    public void Badge_is_empty_for_a_job_that_never_ran(byte outcome) =>
        Assert.Null(AgentJobStatus.Badge(outcome, 0));

    [Fact]
    public void LastRun_decodes_the_packed_date_and_time() =>
        Assert.Equal("Last run 2026-08-05 14:30:05", AgentJobStatus.LastRun(20260805, 143005));

    // A run before 10:00 packs to a 5-digit int (93005 = 09:30:05) — the digits must not shift.
    [Fact]
    public void LastRun_pads_a_time_before_ten() =>
        Assert.Equal("Last run 2026-01-09 09:30:05", AgentJobStatus.LastRun(20260109, 93005));

    [Fact]
    public void LastRun_is_empty_for_a_job_that_never_ran() =>
        Assert.Null(AgentJobStatus.LastRun(0, 0));
}
