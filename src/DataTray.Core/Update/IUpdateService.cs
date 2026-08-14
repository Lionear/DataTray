namespace DataTray.Core.Update;

/// <summary>
/// Checking for, downloading and applying a new build of the app (SE-245). Behind an interface so the
/// view models can be exercised without a managed install — the real implementation only does anything
/// inside one, which by definition never holds in a test run or the screenshot renderer.
/// </summary>
public interface IUpdateService
{
    /// <summary>Whether this install can replace itself, and why not when it cannot.</summary>
    UpdateSupport Support { get; }

    /// <summary>
    /// The version running right now. Read from the installed package's manifest where there is one,
    /// because that is the number the updater compares the feed against — anything else can disagree
    /// with it, and then "it keeps offering the build I just installed" looks like a wrong label
    /// instead of the mismatch it actually is.
    /// </summary>
    string CurrentVersion { get; }

    /// <summary>
    /// The channel this build was published on, parsed from its own version stamp. What a fresh
    /// install follows until the user picks something else — downloading a nightly is itself a choice.
    /// </summary>
    UpdateChannel BuildChannel { get; }

    /// <summary>
    /// Is there something newer on <paramref name="channel"/>? Fault-tolerant by contract: a check
    /// that cannot reach the network yields <see cref="UpdateStatus.Failed"/> rather than throwing,
    /// because an update check is a background errand and offline is not an error the user asked about.
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken ct);

    /// <summary>
    /// What <paramref name="channel"/> currently offers, with no judgement about whether you should
    /// take it (SE-163). <see cref="CheckAsync"/> answers "is there something newer?" and says no to a
    /// downgrade, so an automatic notification can never present one as an update. A user *choosing* a
    /// channel in Settings is asking a different question and deserves an answer even when the target
    /// is older. Null when the channel cannot be reached.
    /// </summary>
    Task<ChannelOffer?> PeekAsync(UpdateChannel channel, CancellationToken ct);

    /// <summary>
    /// Fetch the build found by the last <see cref="CheckAsync"/>. Progress is a <b>fraction</b>,
    /// 0 to 1 — the scale the banner's progress bar is built for (<c>Maximum="1"</c>). Implementations
    /// reporting a percentage have to divide: a 0-100 value against that bar reads as "full" from the
    /// first percent, which looks exactly like a download that has hung.
    /// </summary>
    Task DownloadAsync(IProgress<double> progress, CancellationToken ct);

    /// <summary>
    /// Apply the downloaded build and relaunch. Does not return on success — the updater replaces the
    /// app and starts it again — so a normal return means nothing was applied. Throws when the apply
    /// fails; the caller reports that rather than the app quietly staying on the old build.
    /// </summary>
    Task ApplyAndRestartAsync();
}
