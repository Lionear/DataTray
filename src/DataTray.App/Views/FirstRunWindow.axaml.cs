using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DataTray.App.ViewModels;

namespace DataTray.App.Views;

/// <summary>
/// The first-run wizard window (SE-239). Shown once on a fresh profile; the view model decides what each
/// step contains and when onboarding counts as done.
/// </summary>
public partial class FirstRunWindow : Window
{
    public FirstRunWindow()
    {
        InitializeComponent();
    }

    public FirstRunWindow(FirstRunViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested = Close;
        // Closing the window at all ends onboarding — including the title-bar X, which is a dismissal like
        // Skip. The one exception (a restart to load a freshly installed engine) is handled in the VM, which
        // keeps its saved position instead.
        Closed += (_, _) => viewModel.FinishCommand.Execute(null);
    }

    // SQLite's database path is a basic field, so the wizard needs the same picker the Connection Manager has.
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ConnectionFieldInput input })
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (files.Count > 0)
        {
            input.Value = files[0].TryGetLocalPath() ?? files[0].Path.ToString();
        }
    }
}
