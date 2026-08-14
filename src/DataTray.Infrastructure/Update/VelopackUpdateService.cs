using System.Runtime.InteropServices;
using DataTray.Core.Store;
using DataTray.Core.Update;
using Velopack;
using Velopack.Sources;

namespace DataTray.Infrastructure.Update;

/// <summary>
/// <see cref="IUpdateService"/> on top of Velopack, reading the feeds the Build workflow publishes to
/// this repository's GitHub releases (SE-245).
/// <para>
/// One <see cref="UpdateManager"/> is built per call rather than once, because the channel is a
/// construction-time option and the user can change it in Settings while the app runs.
/// </para>
/// <para>
/// Barely unit-testable on purpose: everything here needs a real managed install, and constructing the
/// manager throws without one. The parts that <i>are</i> testable — the channel name and the feed URL —
/// live in <c>UpdateChannelExtensions</c> and have their own tests, rather than going untested because
/// the class around them is awkward.
/// </para>
/// </summary>
public sealed class VelopackUpdateService(string runningVersion) : IUpdateService
{
    private const string RepositoryUrl = "https://github.com/Lionear/DataTray";

    private UpdateInfo? _pending;
    private UpdateChannel _pendingChannel = UpdateChannel.Stable;
    private UpdateSupport? _support;

    /// <summary>
    /// Velopack only manages an install it created. A build directory, an unpacked archive or a
    /// distro package all report <see cref="UpdateSupport.NotPackaged"/>, and the UI then points at
    /// the download page rather than offering a restart that would do nothing.
    /// <para>
    /// Answered once and remembered: bindings read this repeatedly and it cannot change while the
    /// process runs. Constructing the manager throws in a host that never called
    /// <c>VelopackApp.Run()</c> — a test, the screenshot renderer, anything embedding these view
    /// models — and a property that throws inside a binding fails <b>silently</b>, leaving the control
    /// at its default visibility. So the throw is caught and read as "not packaged", which is exactly
    /// what such a host is.
    /// </para>
    /// </summary>
    public UpdateSupport Support => _support ??= Probe();

    private static UpdateSupport Probe()
    {
        try
        {
            return new UpdateManager(new SimpleWebSource(RepositoryUrl)).IsInstalled
                ? UpdateSupport.Supported
                : UpdateSupport.NotPackaged;
        }
        catch (Exception)
        {
            return UpdateSupport.NotPackaged;
        }
    }

    public string CurrentVersion => ReadCurrentVersion() ?? runningVersion;

    // Preferably the installed package's manifest: that is the number CheckForUpdatesAsync compares
    // the feed against. An install the updater does not manage has no manifest, and there the build's
    // own stamp is both the honest answer and the only one.
    private string? ReadCurrentVersion()
    {
        try
        {
            var manager = new UpdateManager(new SimpleWebSource(RepositoryUrl));
            return manager.IsInstalled ? manager.CurrentVersion?.ToFullString() : null;
        }
        catch (Exception)
        {
            // Same reason Probe() swallows: no VelopackApp in this host.
            return null;
        }
    }

    /// <summary>
    /// Read from the version this build carries — the full string the workflow stamped, e.g.
    /// <c>0.8.0-nightly.20260814.99</c>. The assembly <em>version</em> cannot answer this: it is four
    /// integers, so the pre-release tag that names the channel is already gone by the time it is written.
    /// </summary>
    public UpdateChannel BuildChannel =>
        ChannelStamp.TryParse(runningVersion, out var stamp) ? stamp.Channel : UpdateChannel.Stable;

    public async Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken ct)
    {
        if (Support != UpdateSupport.Supported)
        {
            return UpdateCheckResult.UpToDate;
        }

        try
        {
            var info = await ManagerFor(channel).CheckForUpdatesAsync().WaitAsync(ct);
            _pending = info;
            _pendingChannel = channel;

            return info is null ? UpdateCheckResult.UpToDate : UpdateCheckResult.Available(Describe(info));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Offline, a 404 on the feed, a malformed index — an update check is a background errand,
            // so every one of those is silent rather than a dialog the user did not ask for.
            return UpdateCheckResult.Failed;
        }
    }

    public async Task<ChannelOffer?> PeekAsync(UpdateChannel channel, CancellationToken ct)
    {
        if (Support != UpdateSupport.Supported)
        {
            return null;
        }

        try
        {
            var info = await ManagerFor(channel).CheckForUpdatesAsync().WaitAsync(ct);
            _pending = info;
            _pendingChannel = channel;

            var build = info is null ? null : Describe(info);
            return new ChannelOffer(channel, build, CurrentVersion, CompareCores(build?.Version));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public Task DownloadAsync(IProgress<double> progress, CancellationToken ct)
    {
        var info = _pending
            ?? throw new InvalidOperationException("No update has been found to download.");

        // Velopack reports whole percents; the banner's bar has Maximum="1". Without the divide it sits
        // full from the first percent on, which is indistinguishable from a stalled download.
        return ManagerFor(_pendingChannel)
            .DownloadUpdatesAsync(info, percent => progress.Report(percent / 100.0), ct);
    }

    public Task ApplyAndRestartAsync()
    {
        var info = _pending
            ?? throw new InvalidOperationException("No update has been downloaded.");

        ManagerFor(_pendingChannel).ApplyUpdatesAndRestart(info.TargetFullRelease);
        return Task.CompletedTask;
    }

    private static OfferedBuild Describe(UpdateInfo info)
    {
        var target = info.TargetFullRelease;
        return new OfferedBuild(target.Version.ToFullString(), target.Size, target.NotesMarkdown);
    }

    // Sign of the offered core against the running one. Only the numeric core counts: the pre-release
    // tag names the channel, and comparing that as text would call a channel switch a downgrade.
    private int CompareCores(string? offeredVersion)
    {
        if (offeredVersion is null)
        {
            return 0;
        }

        ChannelStamp.TryParse(CurrentVersion, out var running);
        ChannelStamp.TryParse(offeredVersion, out var offered);
        return SemVer.Compare(offered.Core, running.Core);
    }

    private UpdateManager ManagerFor(UpdateChannel channel) =>
        new(new SimpleWebSource(channel.FeedBaseUrl(RepositoryUrl)), OptionsFor(channel, BuildChannel));

    /// <summary>
    /// Without <see cref="UpdateOptions.AllowVersionDowngrade"/> the updater answers "no updates" for
    /// every channel whose newest build sorts below the running one — and the channel is part of the
    /// version, compared as text, so <c>0.8.0-nightly.x</c> sorts below <c>0.8.0-preview.x</c>.
    /// Switching preview to nightly would therefore be silently impossible.
    /// <para>
    /// Allowed only when the target channel is not the one this build came from. A switch is a jump to
    /// a different stream that the user asked for by name; the default guards against a feed on your
    /// <em>own</em> channel rolling backwards, and that stays guarded (SE-162).
    /// </para>
    /// </summary>
    internal static UpdateOptions OptionsFor(UpdateChannel channel, UpdateChannel buildChannel) => new()
    {
        ExplicitChannel = channel.VelopackChannel(CurrentRid()),
        AllowVersionDowngrade = channel != buildChannel
    };

    /// <summary>The RID naming the build assets and the Velopack channels use (win-x64, osx-arm64, …).</summary>
    internal static string CurrentRid()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            var other => other.ToString().ToLowerInvariant()
        };
        return $"{os}-{arch}";
    }
}
