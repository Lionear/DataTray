using DataTray.Core.Localization;
using DataTray.Core.Update;

namespace DataTray.App.ViewModels;

/// <summary>
/// Backs the changelog dialog for an available update (SE-137 / SE-151): the new build's version and
/// release notes. Downloading and installing live in the banner itself (<see cref="AppUpdateViewModel"/>),
/// so this dialog is notes-only — one source of truth for download status.
/// <para>
/// The publish date and commit lines are gone with SE-245: those came from our own update manifest,
/// and a Velopack feed carries neither. Version and notes are what it does carry, and they were the
/// two the dialog was actually for.
/// </para>
/// </summary>
public sealed class UpdateAvailableViewModel : ViewModelBase
{
    private readonly OfferedBuild _build;

    public UpdateAvailableViewModel(OfferedBuild build, ILocalizer localizer)
    {
        _build = build;
        Loc = localizer;
    }

    public ILocalizer Loc { get; }

    public string VersionLine => Loc.Get("UpdateDialogVersion", _build.Version);

    /// <summary>Raw markdown notes; the view renders them via <c>MiniMarkdown</c>.</summary>
    public string Notes => _build.NotesMarkdown ?? string.Empty;
}
