using System.ComponentModel;
using System.Globalization;
using DataTray.App.ViewModels;
using DataTray.Core.Localization;
using DataTray.Core.Settings;
using DataTray.Core.Update;

namespace DataTray.App.Tests;

// SE-301: the updater's apply call ends the process itself (Velopack exits), so the window's OnClosing —
// where the session is written on a normal quit — never runs. The banner has to save the session first.
public class AppUpdateSessionSaveTests
{
    [Fact]
    public async Task InstallAndRestart_SavesTheSession_BeforeTheUpdaterEndsTheProcess()
    {
        var events = new List<string>();
        var vm = await ReadyToInstallAsync(new RecordingUpdateService(events));
        vm.SaveSession = () => events.Add("save");

        await vm.InstallAndRestartCommand.ExecuteAsync(null);

        Assert.Equal(["save", "apply"], events);
    }

    [Fact]
    public async Task InstallAndRestart_StillApplies_WhenTheSessionCannotBeSaved()
    {
        var events = new List<string>();
        var vm = await ReadyToInstallAsync(new RecordingUpdateService(events));
        vm.SaveSession = () => throw new IOException("disk full");

        await vm.InstallAndRestartCommand.ExecuteAsync(null);

        Assert.Equal(["apply"], events);
    }

    private static async Task<AppUpdateViewModel> ReadyToInstallAsync(RecordingUpdateService service)
    {
        var vm = new AppUpdateViewModel(service, new FakeSettingsStore(), new FakeLocalizer());
        await vm.RunCheckAsync(UpdateChannel.Stable, CancellationToken.None);
        return vm;
    }

    private sealed class RecordingUpdateService(List<string> events) : IUpdateService
    {
        public UpdateSupport Support => throw new NotSupportedException();
        public string CurrentVersion => "1.0.0";
        public UpdateChannel BuildChannel => UpdateChannel.Stable;

        public Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken ct) =>
            Task.FromResult(UpdateCheckResult.Available(new OfferedBuild("1.1.0", 1, null)));

        public Task<ChannelOffer?> PeekAsync(UpdateChannel channel, CancellationToken ct) => Task.FromResult<ChannelOffer?>(null);
        public Task DownloadAsync(IProgress<double> progress, CancellationToken ct) => Task.CompletedTask;

        public Task ApplyAndRestartAsync()
        {
            events.Add("apply");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        private AppSettings _current = new();
        public AppSettings Load() => _current;
        public void Save(AppSettings settings) => _current = settings;
    }

    private sealed class FakeLocalizer : ILocalizer
    {
        public CultureInfo Culture => CultureInfo.InvariantCulture;
        public string this[string key] => key;
        public string Get(string key, params object[] args) => key;
        public void SetCulture(CultureInfo culture) { }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }
}
