using Avalonia.Controls;
using DataTray.App.ViewModels;

namespace DataTray.App.Views;

public partial class NodeInfoDialog : Window
{
    public NodeInfoDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is NodeInfoDialogViewModel vm)
            {
                vm.CloseRequested = Close;
            }
        };
    }
}
