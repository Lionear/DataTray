using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DataTray.App.Views;

public partial class ImportConnectionsDialog : Window
{
    public ImportConnectionsDialog()
    {
        InitializeComponent();
    }

    private void OnImport(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
