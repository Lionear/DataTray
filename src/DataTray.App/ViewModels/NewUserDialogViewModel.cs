using System.Collections.ObjectModel;
using DataTray.Core.Localization;
using DataTray.Sdk;
using DataTray.Sdk.Security;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DataTray.App.ViewModels;

/// <summary>
/// Backs the generic "New User…" dialog. A name plus the provider's declarative <see cref="UserField"/>s
/// (password/host/attribute toggles) and an optional role checkbox list drive a live, editable SQL preview
/// via the provider's <c>BuildCreateUserStatement</c>. Confirm hands the (possibly user-edited) preview
/// back to <see cref="MainViewModel"/>, which runs it via <c>ExecuteDdlAsync</c> — this dialog never
/// touches the database.
/// </summary>
public partial class NewUserDialogViewModel : ViewModelBase
{
    private IDbProvider _provider = null!;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _sqlPreview = string.Empty;

    public NewUserDialogViewModel(ILocalizer localizer)
    {
        Loc = localizer;
    }

    public ILocalizer Loc { get; }

    public ObservableCollection<UserFieldInput> Fields { get; } = [];

    public ObservableCollection<RoleChoice> Roles { get; } = [];

    public bool HasRoles => Roles.Count > 0;

    /// <summary>A user name is always required; each provider field marked <see cref="UserField.Required"/>
    /// must also be filled.</summary>
    public bool CanSave => !string.IsNullOrWhiteSpace(Name)
        && Fields.All(f => !f.Required || f.Type == UserFieldType.Bool || !string.IsNullOrWhiteSpace(f.Value));

    /// <summary>Reset for a specific provider's user-create form. Mirrors <c>CreateObjectDialogViewModel.Configure</c>
    /// — the VM comes from a DI factory that can't take per-invocation arguments.</summary>
    public void Configure(IDbProvider provider, IReadOnlyList<UserField> fields, IReadOnlyList<string> roles)
    {
        _provider = provider;

        Name = string.Empty;

        Fields.Clear();
        foreach (var field in fields)
        {
            var input = new UserFieldInput(field);
            input.PropertyChanged += (_, _) => RefreshPreview();
            Fields.Add(input);
        }

        Roles.Clear();
        foreach (var role in roles)
        {
            var choice = new RoleChoice(role);
            choice.PropertyChanged += (_, _) => RefreshPreview();
            Roles.Add(choice);
        }

        OnPropertyChanged(nameof(HasRoles));
        RefreshPreview();
    }

    partial void OnNameChanged(string value) => RefreshPreview();

    private void RefreshPreview()
    {
        OnPropertyChanged(nameof(CanSave));

        if (string.IsNullOrWhiteSpace(Name))
        {
            SqlPreview = string.Empty;
            return;
        }

        var values = new Dictionary<string, string?> { ["name"] = Name };
        foreach (var field in Fields)
        {
            values[field.Key] = field.Type == UserFieldType.Bool
                ? field.BoolValue ? "true" : "false"
                : field.Value;
        }

        var selectedRoles = Roles.Where(r => r.IsSelected).Select(r => r.Name).ToList();

        try
        {
            SqlPreview = _provider.BuildCreateUserStatement(values, selectedRoles).Text;
        }
        catch (Exception ex)
        {
            SqlPreview = $"-- {ex.Message}";
        }
    }
}

/// <summary>One input row in the New User dialog, wrapping a provider <see cref="UserField"/>. Exposes
/// per-type visibility flags so a single DataTemplate can render text/password/bool/choice inputs.</summary>
public sealed partial class UserFieldInput : ObservableObject
{
    public UserFieldInput(UserField field)
    {
        Key = field.Key;
        Label = field.Label;
        Type = field.Type;
        Required = field.Required;
        Hint = field.Hint;
        Choices = field.Choices ?? [];
        _value = field.Default ?? (Choices.Count > 0 ? Choices[0] : string.Empty);
        _boolValue = field.Default == "true";
    }

    public string Key { get; }

    public string Label { get; }

    public UserFieldType Type { get; }

    public bool Required { get; }

    public string? Hint { get; }

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    public IReadOnlyList<string> Choices { get; }

    public bool IsText => Type == UserFieldType.Text;

    public bool IsPassword => Type == UserFieldType.Password;

    public bool IsBool => Type == UserFieldType.Bool;

    public bool IsChoice => Type == UserFieldType.Choice;

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    private bool _boolValue;
}

/// <summary>One selectable role in the New User dialog's checkbox list.</summary>
public sealed partial class RoleChoice(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isSelected;
}
