using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DataTray.App.ViewModels;

namespace DataTray.App.Views;

public partial class AboutWindow : Window
{
    // Project links shown in the footer. Hard-coded on purpose: they identify this product, they are not
    // user settings, and About is the one place they belong.
    private const string WebsiteUrl = "https://lionear.dev";
    private const string RepositoryUrl = "https://github.com/Lionear/SqlExplorer";
    private const string LicenseUrl = "https://github.com/Lionear/SqlExplorer/blob/main/LICENSE";
    private const string ThirdPartyUrl = "https://github.com/Lionear/SqlExplorer/blob/main/THIRD-PARTY-NOTICES.md";

    public AboutWindow()
    {
        InitializeComponent();
    }

    public AboutWindow(AboutViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.ClipboardRequested = CopyToClipboardAsync;
    }

    private async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void OnWebsite(object? sender, RoutedEventArgs e) => OpenUrl(WebsiteUrl);

    private void OnRepository(object? sender, RoutedEventArgs e) => OpenUrl(RepositoryUrl);

    private void OnLicense(object? sender, RoutedEventArgs e) => OpenUrl(LicenseUrl);

    private void OnThirdParty(object? sender, RoutedEventArgs e) => OpenUrl(ThirdPartyUrl);

    // Best-effort: a missing/blocked browser must not take the dialog down with it.
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}
