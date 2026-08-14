namespace DataTray.Core.Update;

/// <summary>
/// What a channel currently offers, from <see cref="IUpdateService.PeekAsync"/> — the answer to "what
/// is on that channel?" rather than "should I update?" (SE-163).
///
/// <para>The distinction is the point. An <i>automatic</i> update notification must never present a
/// lower build as an update, which is why <see cref="IUpdateService.CheckAsync"/> refuses one. A user
/// who picks a channel in Settings has made a deliberate choice, and the honest response to "Stable is
/// older than what you run" is to say so and offer the switch — not silence, which is what they got
/// before.</para>
/// </summary>
/// <param name="Build">The build on that channel, or null when the channel has nothing newer than the
/// running one. The updater reports "nothing to offer" without naming what it found, so this is as
/// specific as the answer gets — see <see cref="HasNothingNewer"/>.</param>
/// <param name="CoreComparedToRunning">Sign of the target core against the running one: negative means
/// switching is a <b>downgrade</b>, zero the same core on another channel, positive a normal update.</param>
public sealed record ChannelOffer(
    UpdateChannel Channel,
    OfferedBuild? Build,
    string RunningVersion,
    int CoreComparedToRunning)
{
    /// <summary>True when taking this channel means moving to an older core — the case that needs
    /// saying out loud and confirming, rather than being applied or silently skipped.</summary>
    public bool IsDowngrade => Build is not null && CoreComparedToRunning < 0;

    /// <summary>
    /// The channel was reached and has nothing newer than the running build. Replaces the old
    /// "offers exactly the build you already run": the updater answers a check with a build or with
    /// nothing, and never names the version it decided against, so those two cases are no longer
    /// distinguishable. Unreachable is a separate case and yields a null offer, not this.
    /// </summary>
    public bool HasNothingNewer => Build is null;

    /// <summary>Whether there is actually something to install if the user accepts.</summary>
    public bool CanInstall => Build is not null;

    /// <summary>The offered version, or an empty string when the channel had nothing to offer.</summary>
    public string Version => Build?.Version ?? string.Empty;
}
