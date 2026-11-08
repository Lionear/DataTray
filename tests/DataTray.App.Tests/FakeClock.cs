namespace DataTray.App.Tests;

/// <summary>A clock the test moves by hand, so time-based thresholds are tested without waiting.</summary>
internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 8, 9, 41, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
