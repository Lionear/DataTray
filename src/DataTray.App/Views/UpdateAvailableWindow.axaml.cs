using Avalonia.Controls;
using Avalonia.Interactivity;
using DataTray.App.Markdown;
using DataTray.App.ViewModels;

namespace DataTray.App.Views;

public partial class UpdateAvailableWindow : Window
{
    public UpdateAvailableWindow()
    {
        InitializeComponent();
    }

    public UpdateAvailableWindow(UpdateAvailableViewModel viewModel) : this()
    {
        DataContext = viewModel;

        // The notes are markdown; render them once into the placeholder (there's no in-app markdown control).
        // Download/install now live in the main-window banner (SE-151), so this dialog is notes-only.
        NotesHost.Child = MiniMarkdown.Render(viewModel.Notes);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
