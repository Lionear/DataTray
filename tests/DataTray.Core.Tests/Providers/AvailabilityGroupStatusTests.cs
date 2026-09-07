using DataTray.Providers.MsSql;

namespace DataTray.Core.Tests.Providers;

/// <summary>
/// SE-284's dashboard has no live availability group to test against in this suite (no fixture models
/// Always On DMV content — see <c>Codebase.md</c>), so the derivation/decoding logic that can be pinned
/// without one lives here: the "behind by" arithmetic, the readable/suspended wording, and the backup
/// preference and health-banner text.
/// </summary>
public class AvailabilityGroupStatusTests
{
    [Fact]
    public void BehindBy_is_null_for_the_primarys_own_row()
    {
        var now = new DateTime(2026, 9, 7, 11, 42, 0);
        Assert.Null(AvailabilityGroupStatus.BehindBy(isPrimaryRow: true, now, now.AddMinutes(-5)));
    }

    [Fact]
    public void BehindBy_is_null_when_either_timestamp_is_unknown()
    {
        var now = new DateTime(2026, 9, 7, 11, 42, 0);
        Assert.Null(AvailabilityGroupStatus.BehindBy(isPrimaryRow: false, null, now));
        Assert.Null(AvailabilityGroupStatus.BehindBy(isPrimaryRow: false, now, null));
    }

    [Fact]
    public void BehindBy_reports_zero_for_a_caught_up_secondary()
    {
        var now = new DateTime(2026, 9, 7, 11, 42, 7);
        Assert.Equal("0s", AvailabilityGroupStatus.BehindBy(isPrimaryRow: false, now, now));
    }

    [Fact]
    public void BehindBy_reports_seconds_under_a_minute()
    {
        var primary = new DateTime(2026, 9, 7, 11, 42, 7);
        var secondary = primary.AddSeconds(-41);
        Assert.Equal("41s", AvailabilityGroupStatus.BehindBy(isPrimaryRow: false, primary, secondary));
    }

    [Fact]
    public void BehindBy_reports_minutes_and_seconds_under_an_hour()
    {
        var primary = new DateTime(2026, 9, 7, 11, 42, 7);
        var secondary = primary.AddMinutes(-4).AddSeconds(-8);
        Assert.Equal("4m 08s", AvailabilityGroupStatus.BehindBy(isPrimaryRow: false, primary, secondary));
    }

    [Fact]
    public void BehindBy_reports_hours_and_minutes_over_an_hour()
    {
        var primary = new DateTime(2026, 9, 7, 11, 42, 55);
        var secondary = primary.AddHours(-1).AddMinutes(-44);
        Assert.Equal("1h 44m", AvailabilityGroupStatus.BehindBy(isPrimaryRow: false, primary, secondary));
    }

    // A secondary's own last_commit_time can briefly read later than the primary's snapshot (the two reads
    // are not transactionally consistent) — that must not surface as a negative duration.
    [Fact]
    public void BehindBy_clamps_a_negative_gap_to_zero()
    {
        var primary = new DateTime(2026, 9, 7, 11, 42, 0);
        var secondary = primary.AddSeconds(3);
        Assert.Equal("0s", AvailabilityGroupStatus.BehindBy(isPrimaryRow: false, primary, secondary));
    }

    [Fact]
    public void Readable_is_always_No_for_the_primarys_own_row() =>
        Assert.Equal("No", AvailabilityGroupStatus.Readable("PRIMARY", "ALL"));

    [Theory]
    [InlineData("ALL", "Yes (all)")]
    [InlineData("READ_ONLY", "Yes (read-intent)")]
    [InlineData("NO", "No")]
    [InlineData(null, "No")]
    public void Readable_decodes_a_secondarys_allow_connections(string? allow, string expected) =>
        Assert.Equal(expected, AvailabilityGroupStatus.Readable("SECONDARY", allow));

    [Fact]
    public void Suspended_is_plain_No_when_not_suspended() =>
        Assert.Equal("No", AvailabilityGroupStatus.Suspended(false, "USER_ACTION"));

    [Fact]
    public void Suspended_names_the_reason() =>
        Assert.Equal("Yes — USER_ACTION", AvailabilityGroupStatus.Suspended(true, "USER_ACTION"));

    [Fact]
    public void Suspended_degrades_gracefully_without_a_reason() =>
        Assert.Equal("Yes — unknown reason", AvailabilityGroupStatus.Suspended(true, null));

    [Theory]
    [InlineData("PRIMARY", "Prefer primary")]
    [InlineData("SECONDARY_ONLY", "Secondary only")]
    [InlineData("SECONDARY", "Prefer secondary")]
    [InlineData("NONE", "None")]
    public void BackupPreferenceText_translates_the_server_constant(string desc, string expected) =>
        Assert.Equal(expected, AvailabilityGroupStatus.BackupPreferenceText(desc));

    [Fact]
    public void Summary_names_the_primary_when_healthy() =>
        Assert.Equal("Healthy. Primary is SQL01\\PROD.", AvailabilityGroupStatus.Summary("SQL01\\PROD", "HEALTHY"));

    [Fact]
    public void Summary_does_not_read_partially_healthy_as_an_alarm() =>
        Assert.DoesNotContain("Not healthy", AvailabilityGroupStatus.Summary("SQL01\\PROD", "PARTIALLY_HEALTHY"));

    [Fact]
    public void Summary_flags_not_healthy() =>
        Assert.StartsWith("Not healthy.", AvailabilityGroupStatus.Summary("SQL01\\PROD", "NOT_HEALTHY"));

    [Fact]
    public void Summary_falls_back_to_naming_the_primary_when_health_is_unknown() =>
        Assert.Equal("Primary is SQL01\\PROD.", AvailabilityGroupStatus.Summary("SQL01\\PROD", null));
}
