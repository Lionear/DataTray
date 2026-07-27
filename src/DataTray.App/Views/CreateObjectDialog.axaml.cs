using Avalonia.Controls;
using Avalonia.Interactivity;
using DataTray.App.ViewModels;

namespace DataTray.App.Views;

public partial class CreateObjectDialog : Window
{
    public CreateObjectDialog()
    {
        InitializeComponent();
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CreateObjectDialogViewModel { CanSave: true } vm)
        {
            Close(vm.SqlPreview);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
