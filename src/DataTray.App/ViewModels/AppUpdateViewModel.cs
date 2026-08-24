using System.Threading;
using DataTray.Core.Localization;
using DataTray.Core.Settings;
using DataTray.Core.Update;
using DataTray.Infrastructure.Update;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DataTray.App.ViewModels;

/// <summary>The update banner's state machine (SE-151): the download + install confirmation live in the
/// banner itself, so it walks Available → Downloading → ReadyToInstall → (install &amp; restart), with
/// Failed as the error branch.
/// <para>
/// The old <c>Guided</c> state is gone with SE-245. It existed for the two cases the previous updater
/// could not finish by itself — the macOS DMG drag, and an asset this platform could not apply in place
/// — and Velopack has neither: it applies every platform in place and restarts the app itself.
/// </para>
/// </summary>
public enum BannerState { Available, Downloading, ReadyToInstall, Failed }

/// <summary>
/// The shared brain for the in-app updater's UI (SE-137): a single instance behind both the main-window
/// banner and the Settings "Check for updates" button, so a manual check lights up the same banner and the
/// same "What's new" action. Runs the check on startup, then periodically while the app stays open, then
/// on demand — always fault-tolerant (offline is a silent no-op). Downloading and installing happen inline
/// in the banner (SE-151); the changelog dialog is notes-only.
/// </summary>
public sealed partial class AppUpdateViewModel : ViewModelBase
{
    private readonly IUpdateService _service;
    private readonly IAppSettingsStore _settingsStore;

    private UpdateCheckResult? _current;
    private CancellationTokenSource? _downloadCts;

    public AppUpdateViewModel(IUpdateService service, IAppSettingsStore settingsStore, ILocalizer localizer)
    {
        _service = service;
        _settingsStore = settingsStore;
        Loc = localizer;
    }

    public ILocalizer Loc { get; }

    /// <summary>Info-level messages for the Output panel — the update-check cadence and its result. Wired by
    /// <see cref="MainViewModel"/>; null before wiring, so a check never fails on an unwired sink.</summary>
    public Action<string>? Reported { get; set; }

    /// <summary>Set by the view: shows the changelog dialog for the offered build.</summary>
    public Func<UpdateAvailableViewModel, Task>? ChangelogRequested { get; set; }

    /// <summary>Set by the view: confirms removing the leftover pre-Velopack Windows install (SE-245).
    /// Without a hook the removal simply does not happen — never silently, since it takes away an
    /// install the user still has.</summary>
    public Func<Task<bool>>? ConfirmRemoveLegacyInstall { get; set; }

    /// <summary>The channel of the running build — the default a fresh install follows until one is chosen.</summary>
    public UpdateChannel RunningChannel => _service.BuildChannel;

    /// <summary>Whether this install can replace itself; Settings tells the user when it cannot.</summary>
    public UpdateSupport Support => _service.Support;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private string _bannerText = string.Empty;

    // The inline download/install state (SE-151). The IsX bools drive which banner variant shows.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(IsDownloading), nameof(IsReadyToInstall),
        nameof(IsFailed), nameof(CanDownload))]
    private BannerState _state = BannerState.Available;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool IsAvailable => State == BannerState.Available;
    public bool IsDownloading => State == BannerState.Downloading;
    public bool IsReadyToInstall => State == BannerState.ReadyToInstall;
    public bool IsFailed => State == BannerState.Failed;

    /// <summary>Whether starting a download is the useful action right now. Failed counts: retrying
    /// <em>is</em> downloading again, which is why the banner's Retry command is <c>Download</c>. Settings
    /// shows one button for both (SE-266) rather than a second one that appears only after a failure.</summary>
    public bool CanDownload => IsAvailable || IsFailed;

    /// <summary>The offered build's version, for Settings' inline status.</summary>
    public string? OfferedVersion => _current?.Build?.Version;

    /// <summary>Runs once at startup when auto-check is on; fetch failure is silent (offline = no banner).</summary>
    public async Task CheckOnStartupAsync(CancellationToken ct)
    {
        var settings = _settingsStore.Load();
        DetectLegacyInstall(settings);

        if (settings.CheckForUpdatesOnStartup)
        {
            await CheckEffectiveAsync(settings, ct);
        }
    }

    // --- Leftover pre-Velopack Windows install (SE-245) --------------------------------------------

    private string? _legacyUninstaller;

    /// <summary>True when an Inno-installed DataTray is still registered beside this one. Windows only,
    /// and only inside a managed install — from a build directory the "old" install may well be the one
    /// the user actually uses.</summary>
    [ObservableProperty]
    private bool _hasLegacyInstall;

    private void DetectLegacyInstall(AppSettings settings)
    {
        if (settings.LegacyInstallNoticeDismissed || _service.Support != UpdateSupport.Supported)
        {
            return;
        }

        _legacyUninstaller = LegacyWindowsInstall.FindUninstaller();
        HasLegacyInstall = _legacyUninstaller is not null;
    }

    /// <summary>
    /// Run the old installer's uninstaller, after an explicit yes. The notice does not come back either
    /// way: removing it settles the question, and so does declining it.
    /// </summary>
    [RelayCommand]
    private async Task RemoveLegacyInstall()
    {
        if (_legacyUninstaller is not { } uninstaller || ConfirmRemoveLegacyInstall is null)
        {
            return;
        }

        if (!await ConfirmRemoveLegacyInstall())
        {
            return;
        }

        try
        {
            LegacyWindowsInstall.Remove(uninstaller);
            Reported?.Invoke(Loc["UpdateLegacyInstallRemoving"]);
        }
        catch (Exception ex)
        {
            Reported?.Invoke(ex.Message);
        }

        DismissLegacyInstall();
    }

    /// <summary>Wave the notice away for good — the user has seen it and chosen to keep both.</summary>
    [RelayCommand]
    private void DismissLegacyInstall()
    {
        var settings = _settingsStore.Load();
        settings.LegacyInstallNoticeDismissed = true;
        _settingsStore.Save(settings);
        HasLegacyInstall = false;
    }

    // Floor on the configurable re-check interval, so a mis-set value can't hammer the update server.
    private static readonly TimeSpan MinCheckInterval = TimeSpan.FromMinutes(30);

    /// <summary>While the app stays open (notably close-to-tray), re-check on the interval configured in
    /// Settings (<see cref="AppSettings.UpdateCheckIntervalMinutes"/>), gated on the same auto-check setting.
    /// The interval is re-read every iteration, so a change in Settings takes effect without a restart; 0
    /// disables periodic checks. Respects the "Later" dismissal so it never re-nags a version.</summary>
    public async Task RunPeriodicChecksAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var minutes = _settingsStore.Load().UpdateCheckIntervalMinutes;
                // When periodic checks are off, idle at the floor and re-read — so re-enabling in Settings
                // resumes without a restart, rather than blocking forever on a disabled interval.
                var delay = minutes <= 0 ? MinCheckInterval : TimeSpan.FromMinutes(Math.Max(minutes, MinCheckInterval.TotalMinutes));
                await Task.Delay(delay, ct);

                var settings = _settingsStore.Load();  // may have changed during the delay
                if (settings.CheckForUpdatesOnStartup && settings.UpdateCheckIntervalMinutes > 0 && !HasUpdate)
                {
                    await CheckEffectiveAsync(settings, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — stop quietly.
        }
    }

    /// <summary>Manual check (Settings): surfaces the result even if previously dismissed, and returns the
    /// status for an inline message. Lights the banner too, so it's there once Settings closes.</summary>
    public async Task<UpdateStatus> RunCheckAsync(UpdateChannel channel, CancellationToken ct)
    {
        var result = await _service.CheckAsync(channel, ct);
        if (result is { IsAvailable: true, Build: not null })
        {
            Surface(result);
        }

        return result.Status;
    }

    /// <summary>What a channel currently offers, regardless of whether it's newer (SE-163). Settings uses it
    /// to tell the user what picking that channel would actually mean, before it means it.</summary>
    public Task<ChannelOffer?> PeekChannelAsync(UpdateChannel channel, CancellationToken ct) =>
        _service.PeekAsync(channel, ct);

    /// <summary>
    /// Put a channel offer the user deliberately chose into the banner's download/install flow — including a
    /// <b>downgrade</b>, which <see cref="RunCheckAsync"/> would never surface on its own.
    ///
    /// <para>That asymmetry is deliberate rather than an inconsistency: an automatic check must never present
    /// an older build as an update, but a user who confirmed "switch and downgrade" has already been told
    /// exactly what it means. The intent travels as this one call, so the rule in the service stays as strict
    /// as it was.</para>
    /// </summary>
    public void SurfaceChosen(ChannelOffer offer)
    {
        if (offer.Build is { } build)
        {
            Surface(UpdateCheckResult.Available(build));
        }
    }

    /// <summary>Builds the notes-only changelog dialog VM for the current offer (or null if there's none).</summary>
    public UpdateAvailableViewModel? BuildDialog() =>
        _current is { Build: { } build } ? new UpdateAvailableViewModel(build, Loc) : null;

    private async Task CheckEffectiveAsync(AppSettings settings, CancellationToken ct)
    {
        var channel = settings.UpdateChannel ?? _service.BuildChannel;
        var result = await _service.CheckAsync(channel, ct);

        if (result.Status == UpdateStatus.Failed)
        {
            Reported?.Invoke(Loc.Get("UpdateLogFailed", channel));
            return;
        }

        if (result is not { IsAvailable: true, Build: { } build })
        {
            Reported?.Invoke(Loc.Get("UpdateLogUpToDate", channel));
            return;
        }

        if (string.Equals(build.Version, settings.DismissedUpdateVersion, StringComparison.OrdinalIgnoreCase))
        {
            Reported?.Invoke(Loc.Get("UpdateLogDismissed", channel, build.Version));
            return;
        }

        Reported?.Invoke(Loc.Get("UpdateLogAvailable", channel, build.Version));
        Surface(result);
    }

    private void Surface(UpdateCheckResult result)
    {
        _current = result;
        BannerText = Loc.Get("UpdateBannerAvailable", result.Build!.Version);
        OnPropertyChanged(nameof(OfferedVersion));
        State = BannerState.Available;
        HasUpdate = true;
    }

    [RelayCommand]
    private async Task ViewChangelog()
    {
        var dialog = BuildDialog();
        if (dialog is not null && ChangelogRequested is not null)
        {
            await ChangelogRequested(dialog);
        }
    }

    /// <summary>Snooze: remember the version so the banner stays hidden until a newer build appears.</summary>
    [RelayCommand]
    private void Later()
    {
        if (_current?.Build is { } build)
        {
            var settings = _settingsStore.Load();
            settings.DismissedUpdateVersion = build.Version;
            _settingsStore.Save(settings);
        }

        HasUpdate = false;
    }

    // --- Inline download + install (SE-151) --------------------------------------------------------

    /// <summary>
    /// Download the offered build inline in the banner, then wait for the user to confirm the restart.
    /// Cancel → back to Available.
    /// <para>
    /// The SE-153 re-fetch is gone with SE-245: it existed because a rolling asset URL could 404 between
    /// the check and the download, and the updater now resolves the package through its own feed rather
    /// than a URL we handed it.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task Download()
    {
        if (_current?.Build is not { } build)
        {
            return;
        }

        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;
        State = BannerState.Downloading;
        DownloadProgress = 0;
        StatusMessage = Loc.Get("UpdateBannerDownloading", build.Version);

        try
        {
            await _service.DownloadAsync(new Progress<double>(p => DownloadProgress = p), ct);
            State = BannerState.ReadyToInstall;
            StatusMessage = Loc.Get("UpdateBannerReady", build.Version);
        }
        catch (OperationCanceledException)
        {
            State = BannerState.Available;
        }
        catch (Exception ex)
        {
            State = BannerState.Failed;
            StatusMessage = ex.Message;
        }
        finally
        {
            _downloadCts = null;
        }
    }

    /// <summary>Cancel an in-flight download.</summary>
    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    /// <summary>Retry after a failed download.</summary>
    [RelayCommand]
    private Task Retry() => Download();

    /// <summary>
    /// Apply the downloaded build and let the updater relaunch the app. On success this does not return —
    /// the process is replaced — so reaching the line after it means the apply failed, and that is
    /// reported rather than left as an app that quietly stayed on the old build.
    /// </summary>
    [RelayCommand]
    private async Task InstallAndRestart()
    {
        if (_current?.Build is null)
        {
            return;
        }

        StatusMessage = Loc["UpdateDialogInstalling"];

        try
        {
            await _service.ApplyAndRestartAsync();
        }
        catch (Exception ex)
        {
            State = BannerState.Failed;
            StatusMessage = ex.Message;
        }
    }
}
