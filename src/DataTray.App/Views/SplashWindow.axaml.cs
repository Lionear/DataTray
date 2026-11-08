using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DataTray.App.ViewModels;

namespace DataTray.App.Views;

/// <summary>The startup splash (SE-297). Ticks its view model once a second so "slow" is measured live.</summary>
public partial class SplashWindow : Window
{
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SplashViewModel? _viewModel;

    public SplashWindow()
    {
        InitializeComponent();
    }

    public SplashWindow(SplashViewModel viewModel, bool compact, bool reducedMotion) : this()
    {
        DataContext = _viewModel = viewModel;
        Classes.Set("reduced-motion", reducedMotion);

        var version = DependencyInjection.AppServices.RunningVersion();
        VersionText.Text = version;
        CompactVersion.Text = $" · DataTray {version}";
        if (compact || IsOnSmallScreen())
        {
            Classes.Add("compact");
            CompactVersion.IsVisible = true;
        }

        _ticker.Tick += (_, _) => viewModel.Tick();
        Opened += (_, _) => _ticker.Start();
        Closed += (_, _) => _ticker.Stop();
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    // Under 600 px of usable height the full splash would crowd the screen; the compact one fits anywhere.
    private bool IsOnSmallScreen() =>
        Screens.Primary is { } screen && screen.WorkingArea.Height / screen.Scaling < 600;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Esc means nothing while starting; once a step failed it is the way out, like any dialog.
        if (e.Key == Key.Escape && _viewModel is { IsFailed: true })
        {
            _viewModel.QuitCommand.Execute(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SplashViewModel.Phase) || _viewModel is null)
        {
            return;
        }

        // A failed splash must be findable in the taskbar, and Enter must retry.
        ShowInTaskbar = _viewModel.IsFailed;
        if (_viewModel.IsFailed)
        {
            Dispatcher.UIThread.Post(() => RetryButton.Focus());
        }
    }
}
