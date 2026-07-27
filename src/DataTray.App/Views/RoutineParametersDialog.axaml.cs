using Avalonia.Controls;
using DataTray.App.ViewModels;

namespace DataTray.App.Views;

public partial class RoutineParametersDialog : Window
{
    public RoutineParametersDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RoutineParametersDialogViewModel vm)
            {
                vm.CloseRequested = Close;
            }
        };
    }
}
