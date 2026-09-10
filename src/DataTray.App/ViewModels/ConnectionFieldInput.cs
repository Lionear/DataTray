using DataTray.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DataTray.App.ViewModels;

/// <summary>Editable state for one <see cref="ConnectionField"/> in the connection dialog.</summary>
public partial class ConnectionFieldInput : ObservableObject
{
    public ConnectionFieldInput(ConnectionField field)
    {
        Field = field;
        _value = field.Default;
    }

    public ConnectionField Field { get; }

    [ObservableProperty]
    private string? _value;

    public string Label => Field.Required ? $"{Field.Label} *" : Field.Label;

    /// <summary>The label without the required marker, so the marker can be drawn in its own colour
    /// (SE-287 mockup) while the label keeps wrapping inside the fixed label column.</summary>
    public string LabelText => Field.Label;

    /// <summary>" *" for a required field, empty otherwise — rendered as a separate, red run.</summary>
    public string RequiredMarker => Field.Required ? " *" : string.Empty;

    public string? Watermark => Field.Placeholder;
    public bool IsFile => Field.Type == ConnectionFieldType.File;
    public bool IsBool => Field.Type == ConnectionFieldType.Bool;
    public bool IsChoice => Field.Type == ConnectionFieldType.Choice;

    /// <summary>A number field gets a short box: a port or a timeout in a full-width control reads as if
    /// a long value is expected (SE-287 mockup renders these narrow).</summary>
    public bool IsNumber => Field.Type == ConnectionFieldType.Number;

    /// <summary>Options for a <see cref="ConnectionFieldType.Choice"/> field; empty otherwise.</summary>
    public IReadOnlyList<string> Choices => Field.Choices ?? [];

    // A plain text/number/password field: the only kind that shows the free-text TextBox.
    public bool IsText => Field.Type is ConnectionFieldType.Text or ConnectionFieldType.Password
        or ConnectionFieldType.Number or ConnectionFieldType.File;

    // (char)0 tells the TextBox to show plaintext; a bullet masks secrets.
    public char PasswordChar => Field.Type == ConnectionFieldType.Password ? '•' : '\0';

    public bool BoolValue
    {
        get => bool.TryParse(Value, out var b) && b;
        set => Value = value ? "true" : "false";
    }
}
