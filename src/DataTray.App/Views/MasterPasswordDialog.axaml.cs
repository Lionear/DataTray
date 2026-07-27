using Avalonia.Controls;
using Avalonia.Interactivity;
using DataTray.Core.Localization;

namespace DataTray.App.Views;

public enum MasterPasswordMode
{
    Unlock,
    Set,
    Change
}

/// <summary>Result of the dialog. Fields are populated per mode: Unlock → <see cref="Current"/>;
/// Set → <see cref="NewPassword"/>; Change → both.</summary>
public sealed record MasterPasswordDialogResult(string? Current, string? NewPassword);

/// <summary>
/// One dialog serving all three master-password interactions (unlock, set, change), selected by mode. For
/// Unlock it validates inline via <paramref name="unlockValidator"/> so a wrong password keeps the dialog
/// open (used for the startup gate and the idle re-lock); Set/Change return the typed values for the caller
/// to act on. Password fields are masked TextBoxes (Avalonia has no PasswordBox).
/// </summary>
public partial class MasterPasswordDialog : Window
{
    private readonly MasterPasswordMode _mode;
    private readonly ILocalizer _loc;
    private readonly Func<string, bool>? _unlockValidator;
    private readonly string _headerTitle;

    // Parameterless ctor for the XAML previewer only.
    public MasterPasswordDialog() : this(MasterPasswordMode.Unlock, null!, null)
    {
    }

    public MasterPasswordDialog(MasterPasswordMode mode, ILocalizer loc, Func<string, bool>? unlockValidator)
    {
        _mode = mode;
        _loc = loc;
        _unlockValidator = unlockValidator;
        _headerTitle = loc?[mode switch
        {
            MasterPasswordMode.Set => "MasterPwSetTitle",
            MasterPasswordMode.Change => "MasterPwChangeTitle",
            _ => "MasterPwUnlockTitle"
        }] ?? "Master password";

        InitializeComponent();
        Title = _headerTitle;

        if (loc is not null)
        {
            Configure();
        }
    }

    private void Configure()
    {
        string L(string key) => _loc[key];

        HeaderText.Text = _headerTitle;
        CurrentBox.Text = string.Empty;
        NewBox.Text = string.Empty;
        ConfirmBox.Text = string.Empty;

        switch (_mode)
        {
            case MasterPasswordMode.Unlock:
                DescText.Text = L("MasterPwUnlockDesc");
                CurrentLabel.Text = L("MasterPwPassword");
                NewPanel.IsVisible = false;
                ConfirmPanel.IsVisible = false;
                ConfirmButton.Content = L("MasterPwUnlock");
                CancelButton.Content = L("MasterPwQuit");
                break;

            case MasterPasswordMode.Set:
                DescText.Text = L("MasterPwSetDesc");
                CurrentPanel.IsVisible = false;
                NewLabel.Text = L("MasterPwNew");
                ConfirmLabel.Text = L("MasterPwConfirm");
                ConfirmButton.Content = L("MasterPwSetButton");
                CancelButton.Content = L("Cancel");
                break;

            case MasterPasswordMode.Change:
                DescText.Text = L("MasterPwChangeDesc");
                CurrentLabel.Text = L("MasterPwCurrent");
                NewLabel.Text = L("MasterPwNew");
                ConfirmLabel.Text = L("MasterPwConfirm");
                ConfirmButton.Content = L("MasterPwChangeButton");
                CancelButton.Content = L("Cancel");
                break;
        }
    }

    private void ShowError(string key)
    {
        ErrorText.Text = _loc[key];
        ErrorText.IsVisible = true;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        var current = CurrentBox.Text ?? string.Empty;
        var @new = NewBox.Text ?? string.Empty;
        var confirm = ConfirmBox.Text ?? string.Empty;

        switch (_mode)
        {
            case MasterPasswordMode.Unlock:
                if (current.Length == 0)
                {
                    return;
                }
                if (_unlockValidator is { } validate && !validate(current))
                {
                    ShowError("MasterPwWrong");
                    CurrentBox.Text = string.Empty;
                    return;
                }
                Close(new MasterPasswordDialogResult(current, null));
                break;

            case MasterPasswordMode.Set:
                if (@new.Length == 0)
                {
                    return;
                }
                if (@new != confirm)
                {
                    ShowError("MasterPwMismatch");
                    return;
                }
                Close(new MasterPasswordDialogResult(null, @new));
                break;

            case MasterPasswordMode.Change:
                if (current.Length == 0 || @new.Length == 0)
                {
                    return;
                }
                if (@new != confirm)
                {
                    ShowError("MasterPwMismatch");
                    return;
                }
                Close(new MasterPasswordDialogResult(current, @new));
                break;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
