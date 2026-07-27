using Avalonia.Controls;
using Avalonia.Interactivity;
using DataTray.App.ViewModels;

namespace DataTray.App.Views;

public partial class NewUserDialog : Window
{
    public NewUserDialog()
    {
        InitializeComponent();
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NewUserDialogViewModel { CanSave: true } vm)
        {
            Close(vm.SqlPreview);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
