using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DataTray.App.ViewModels;

namespace DataTray.App.Views;

/// <summary>
/// The connection/folder editor, hosted in the main window's content area (SE-289 — it used to be the
/// right half of the Connection Manager window). The form itself is SE-287's; the only thing moving
/// changed is where the file picker gets its <see cref="IStorageProvider"/> from.
/// </summary>
public partial class ConnectionDetailView : UserControl
{
    public ConnectionDetailView() => InitializeComponent();

    // File-type connection field: pick a path (moved here from the retired ConnectionDialog).
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ConnectionFieldInput input }
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (files.Count > 0)
        {
            input.Value = files[0].TryGetLocalPath() ?? files[0].Path.ToString();
        }
    }
}
