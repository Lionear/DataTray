using Avalonia.Controls;
using DataTray.Core.Localization;
using CommunityToolkit.Mvvm.Input;

namespace DataTray.App.ViewModels;

/// <summary>
/// Backs the lightweight node-info dialog: chrome (title + Close) around a provider-supplied read-only
/// view (<c>ICustomNodeInfoUi.CreateInfoView</c>). Unlike <see cref="ToolDialogViewModel"/> there is no
/// Execute/progress/log — this is purely informational (e.g. SQL Server's Database Properties). Plain VM,
/// constructed directly per open (no DI dependencies beyond the shared localizer).
/// </summary>
public partial class NodeInfoDialogViewModel : ViewModelBase
{
    public NodeInfoDialogViewModel(string title, Control view, ILocalizer localizer, bool viewOwnsActionBar = false)
    {
        Title = title;
        View = view;
        Loc = localizer;
        ShowCloseBar = !viewOwnsActionBar;
    }

    public string Title { get; }

    public Control View { get; }

    /// <summary>False when the provider view brings its own footer — a properties dialog that writes needs
    /// OK/Cancel of its own, and the host's Close row underneath it would be a third button that looks like
    /// it does something different. The <c>SecurityDialog</c> chrome settled this the same way; here the
    /// same view is hosted in the page-rail-sized window instead of growing a third one.</summary>
    public bool ShowCloseBar { get; }

    public ILocalizer Loc { get; }

    /// <summary>Set by the view; called to close the window.</summary>
    public Action? CloseRequested { get; set; }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
