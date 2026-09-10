using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using DataTray.Core.Connections;
using DataTray.Core.Connections.Ssh;
using DataTray.Core.Localization;
using DataTray.Core.Providers;
using DataTray.Sdk;
using DataTray.Sdk.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DataTray.App.ViewModels;

/// <summary>
/// Backs the "new connection" dialog. The field list is rebuilt from the selected provider's
/// declared <see cref="ConnectionField"/>s — the dialog itself knows nothing provider-specific.
/// </summary>
public partial class ConnectionDialogViewModel : ViewModelBase
{
    private readonly ConnectionService _connections;
    private readonly IDbProviderRegistry _providers;
    private string _id = Guid.NewGuid().ToString("N");

    // The plugin that owns this connection (SavedConnection.Origin), or null for a user connection. Captured
    // on load and passed straight back on Save so editing never silently un-manages it (which would drop the
    // "Managed" badge and break the owning plugin's origin-scoped remove).
    private string? _origin;

    // The provider id whose Fields are currently built. Guards OnSelectedProviderChanged against rebuilding
    // (and so resetting every field to the provider defaults) when the ComboBox re-fires SelectedProvider
    // with the same provider — the transient null->value round-trip when the detail view re-attaches during
    // an edit, which was silently discarding the loaded/entered values (SE-174).
    private string? _fieldsProviderId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(HeaderSubtitle))]
    private string _name = "New connection";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderSubtitle))]
    private ProviderOption? _selectedProvider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestSucceeded))]
    [NotifyPropertyChangedFor(nameof(TestFailed))]
    [NotifyPropertyChangedFor(nameof(TestUntried))]
    private string _testResult = string.Empty;

    // The action bar shows a status dot next to the test result (SE-287 mockup). The message alone
    // doesn't say which it was — a provider's failure text is just as long and just as grey as "ok".
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestSucceeded))]
    [NotifyPropertyChangedFor(nameof(TestFailed))]
    [NotifyPropertyChangedFor(nameof(TestUntried))]
    private bool? _testOk;

    public bool TestSucceeded => TestOk == true;
    public bool TestFailed => TestOk == false;
    public bool TestUntried => TestOk is null;

    /// <summary>A connection string the user pasted to prefill the fields (import flow, FR-1).</summary>
    [ObservableProperty]
    private string _importConnectionString = string.Empty;

    /// <summary>Whether the selected provider can parse a connection string (hides the import row if not).</summary>
    [ObservableProperty]
    private bool _supportsImport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DialogTitle))]
    private bool _isEditing;

    /// <summary>Selected accent for this connection (hex, or null for none). Drives the tree flag.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedColorOption))]
    [NotifyPropertyChangedFor(nameof(HasColor))]
    private string? _color;

    /// <summary>Whether the detail header paints its colour bar (SE-287).</summary>
    public bool HasColor => Color is not null;

    /// <summary>Safe mode: block the editable-grid save-flow for this connection.</summary>
    [ObservableProperty]
    private bool _readOnly;

    /// <summary>Optional sidebar folder to group this connection under (blank = ungrouped).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderSubtitle))]
    private string? _folder;

    /// <summary>How much MCP (AI) access this connection grants. Default None (fail-closed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAiWriteWarning))]
    [NotifyPropertyChangedFor(nameof(IsAiAccessNone))]
    [NotifyPropertyChangedFor(nameof(IsAiAccessReadOnly))]
    [NotifyPropertyChangedFor(nameof(IsAiAccessReadWrite))]
    [NotifyPropertyChangedFor(nameof(ShowAiPill))]
    [NotifyPropertyChangedFor(nameof(AiPillText))]
    private AiAccessMode _aiAccess = AiAccessMode.None;

    /// <summary>Hard override that blocks this connection from MCP entirely, regardless of AiAccess.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAiPill))]
    private bool _excludeFromMcp;

    // SE-287: the three AI-access levels as radio buttons instead of a dropdown, so every level (and
    // which one is active) is visible without opening anything — the point of the safety layer is that
    // you can see you granted write access. The setters ignore the false side: an unchecked radio just
    // means another one took over, and honouring it would race the newly checked one back to None.
    public bool IsAiAccessNone
    {
        get => AiAccess == AiAccessMode.None;
        set { if (value) { AiAccess = AiAccessMode.None; } }
    }

    public bool IsAiAccessReadOnly
    {
        get => AiAccess == AiAccessMode.ReadOnly;
        set { if (value) { AiAccess = AiAccessMode.ReadOnly; } }
    }

    public bool IsAiAccessReadWrite
    {
        get => AiAccess == AiAccessMode.ReadWrite;
        set { if (value) { AiAccess = AiAccessMode.ReadWrite; } }
    }

    /// <summary>"AI" pill in the detail header: this connection is actually reachable by the MCP server
    /// (opted in and not hard-excluded), mirroring <see cref="SavedConnection.IsMcpReachable"/>.</summary>
    public bool ShowAiPill => AiAccess != AiAccessMode.None && !ExcludeFromMcp;

    public string AiPillText => Loc.Get("AiPill", Loc[$"AiAccess{AiAccess}"]);

    /// <summary>Second line of the detail header: provider, where it connects, and its folder. Display
    /// only — the well-known keys below cover every host-based provider we ship, and a provider without
    /// them (SQLite) simply gets the shorter line.</summary>
    public string HeaderSubtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (SelectedProvider is { } provider)
            {
                parts.Add(provider.DisplayName);
            }

            var host = FieldValue("host") ?? FieldValue("path");
            if (!string.IsNullOrWhiteSpace(host))
            {
                var user = FieldValue("username");
                var port = FieldValue("port");
                var target = string.IsNullOrWhiteSpace(port) ? host : $"{host}:{port}";
                parts.Add(string.IsNullOrWhiteSpace(user) ? target : $"{user}@{target}");
            }

            if (!string.IsNullOrWhiteSpace(Folder))
            {
                parts.Add(Folder!);
            }

            return string.Join(" · ", parts);
        }
    }

    private string? FieldValue(string key) => Fields.FirstOrDefault(f => f.Field.Key == key)?.Value;

    public IReadOnlyList<AiAccessMode> AiAccessModes { get; } =
        [AiAccessMode.None, AiAccessMode.ReadOnly, AiAccessMode.ReadWrite];

    /// <summary>Warn prominently when AI write-access is granted (extra weight on a prod-coloured
    /// connection) — the visible "are you sure" step before persisting ReadWrite (plan §4).</summary>
    public bool ShowAiWriteWarning => AiAccess == AiAccessMode.ReadWrite;

    // A small fixed palette + "none"; enough to flag prod/staging without a full colour picker. The
    // second half of each pair is the resx key for the colour's name — SE-287 turned the row of unlabelled
    // circles into a named dropdown, which is also what makes "no colour" say what it is instead of being
    // an empty ellipse. The values themselves are unchanged.
    private static readonly (string? Hex, string NameKey)[] Palette =
    [
        (null, "ColorNone"),
        ("#E5484D", "ColorRed"),
        ("#F76B15", "ColorOrange"),
        ("#FFB224", "ColorAmber"),
        ("#30A46C", "ColorGreen"),
        ("#3574F0", "ColorBlue"),
        ("#8E4EC6", "ColorPurple"),
    ];

    public ConnectionDialogViewModel(ConnectionService connections, IDbProviderRegistry providers, ILocalizer localizer)
    {
        _connections = connections;
        _providers = providers;
        Loc = localizer;

        AvailableProviders = providers.All
            .Select(r => new ProviderOption(r.Id, r.Provider.DisplayName))
            .OrderBy(o => o.DisplayName)
            .ToList();
        _selectedProvider = AvailableProviders.FirstOrDefault();
        ColorOptions = Palette.Select(c => new ColorSwatch(c.Hex, localizer[c.NameKey])).ToList();
        RebuildFields();
    }

    /// <summary>The selectable colour swatches (first is "none").</summary>
    public IReadOnlyList<ColorSwatch> ColorOptions { get; }

    /// <summary>Two-way bridge for the colour dropdown; the stored value stays the hex string.</summary>
    public ColorSwatch? SelectedColorOption
    {
        get => ColorOptions.FirstOrDefault(s => string.Equals(s.Value, Color, StringComparison.OrdinalIgnoreCase));
        set => Color = value?.Value;
    }

    public ILocalizer Loc { get; }

    public IReadOnlyList<ProviderOption> AvailableProviders { get; }

    // Fields holds every input (source of truth for Values/CanSave/prefill); the two views below
    // partition it for rendering so the common fields stay uncluttered and the rest tuck into an
    // "Advanced" expander.
    public ObservableCollection<ConnectionFieldInput> Fields { get; } = [];

    /// <summary>Always-visible fields (host/port/credentials).</summary>
    public ObservableCollection<ConnectionFieldInput> BasicFields { get; } = [];

    /// <summary>Fields hidden behind the collapsible "Advanced" section.</summary>
    public ObservableCollection<ConnectionFieldInput> AdvancedFields { get; } = [];

    // SE-287: the same two lists again, but cut into the sub-sections the providers already declare via
    // ConnectionField.Group ("Security", "Connection", "SSH tunnel"). That metadata existed and was
    // thrown away — the form rendered every field as one flat list. Ungrouped fields keep their spot at
    // the top of their section; grouped ones follow under a heading, in declaration order.
    public ObservableCollection<ConnectionFieldGroup> BasicGroups { get; } = [];

    public ObservableCollection<ConnectionFieldGroup> AdvancedGroups { get; } = [];

    /// <summary>What is hiding inside the collapsed Advanced section, named rather than counted:
    /// "Security · Connection · SSH tunnel" (SE-287 mockup). Empty when nothing is grouped.</summary>
    public string AdvancedSummary =>
        string.Join(" · ", AdvancedGroups.Where(g => g.HasTitle).Select(g => g.Title));

    [ObservableProperty]
    private bool _hasAdvancedFields;

    /// <summary>Whether the Advanced section is expanded (auto-opens when an import fills a hidden field).</summary>
    [ObservableProperty]
    private bool _isAdvancedExpanded;

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;

    /// <summary>A provider-supplied Avalonia view for the Advanced section (Route B), or null to use the
    /// host-generated field form. Set when the selected provider implements <see cref="ICustomConnectionUi"/>.</summary>
    [ObservableProperty]
    private Control? _customAdvancedView;

    /// <summary>Whether a provider custom view drives the Advanced section (hides the generated form).</summary>
    [ObservableProperty]
    private bool _hasCustomAdvancedView;

    public string DialogTitle => IsEditing ? Loc["EditConnection"] : Loc["NewConnection"];

    /// <summary>Save is allowed once there is a name, a provider, and every required field is filled.</summary>
    public bool CanSave =>
        !string.IsNullOrWhiteSpace(Name)
        && SelectedProvider is not null
        && Fields.All(f => !f.Field.Required || !string.IsNullOrWhiteSpace(f.Value));

    /// <summary>Switch the dialog to edit an existing connection: prefill everything, keep its id.</summary>
    /// <summary>True when this connection is plugin-managed (has an origin). The form surfaces a warning
    /// because changing its settings here can break the link the owning plugin maintains.</summary>
    public bool IsManaged => !string.IsNullOrEmpty(_origin);

    /// <summary>Localised, origin-named warning shown for a managed connection; empty for a user connection.</summary>
    public string ManagedNotice => IsManaged ? Loc.Get("ManagedConnectionWarning", _origin!) : string.Empty;

    public void LoadForEdit(SavedConnection connection)
    {
        _id = connection.Id;
        _origin = connection.Origin;
        OnPropertyChanged(nameof(IsManaged));
        OnPropertyChanged(nameof(ManagedNotice));
        IsEditing = true;
        Name = connection.Name;
        Color = connection.Color;
        ReadOnly = connection.ReadOnly;
        Folder = connection.Folder;
        AiAccess = connection.AiAccess;
        ExcludeFromMcp = connection.ExcludeFromMcp;
        SelectedProvider = AvailableProviders.FirstOrDefault(o => o.Id == connection.ProviderId) ?? SelectedProvider;

        // Ensure fields match the provider even if SelectedProvider didn't change, then overlay stored values
        // (secrets pulled back from the keychain).
        RebuildFields();
        var values = _connections.GetEditableValues(connection);
        foreach (var field in Fields)
        {
            if (values.TryGetValue(field.Field.Key, out var value))
            {
                field.Value = value;
            }
        }

        // The custom Route B view built in RebuildFields read the defaults; rebuild it now that the
        // stored values are in place so it shows what was actually saved.
        if (HasCustomAdvancedView)
        {
            BuildCustomAdvancedView(_providers.Get(connection.ProviderId));
        }
    }

    partial void OnSelectedProviderChanged(ProviderOption? value)
    {
        // Only rebuild when the provider actually changes. The ComboBox's two-way SelectedItem binding
        // re-fires this with a transient null then the same provider when the detail view re-attaches during
        // an edit; rebuilding then would reset every field to the provider defaults and silently discard the
        // loaded/entered values (SE-174). Same id (or null) => leave the field list and its values alone.
        if (value is not null && value.Id != _fieldsProviderId)
        {
            RebuildFields();
        }

        OnPropertyChanged(nameof(CanSave));
    }

    private void RebuildFields()
    {
        _fieldsProviderId = SelectedProvider?.Id;
        foreach (var field in Fields)
        {
            field.PropertyChanged -= OnFieldChanged;
        }

        Fields.Clear();
        BasicFields.Clear();
        AdvancedFields.Clear();
        BasicGroups.Clear();
        AdvancedGroups.Clear();
        CustomAdvancedView = null;
        HasCustomAdvancedView = false;
        if (SelectedProvider is null)
        {
            HasAdvancedFields = false;
            return;
        }

        var provider = _providers.Get(SelectedProvider.Id);
        // The host's SSH block rides along with the provider's own fields (SE-18) — same value dictionary,
        // same save path — but only for providers that connect to a host; there is nothing to forward for a
        // file-backed engine like SQLite.
        var fields = SshConnectionFields.AppliesTo(provider.ConnectionFields)
            ? [.. provider.ConnectionFields, .. SshConnectionFields.All]
            : provider.ConnectionFields;

        foreach (var field in fields)
        {
            var input = new ConnectionFieldInput(field);
            input.PropertyChanged += OnFieldChanged;
            Fields.Add(input);
            (field.Advanced ? AdvancedFields : BasicFields).Add(input);
        }

        Regroup(BasicFields, BasicGroups);
        Regroup(AdvancedFields, AdvancedGroups);
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(AdvancedSummary));

        BuildCustomAdvancedView(provider);
        // Empty string parses to a (possibly empty) map for supporters, null for providers that don't
        // implement it — a cheap capability probe with no side effects.
        SupportsImport = TryParse(SelectedProvider.Id, string.Empty) is not null;
    }

    // Cut a field list into its declared groups, ungrouped first, preserving declaration order inside
    // each. Group values are the provider's own English strings; they get a resx pass here because this
    // is the first time they are shown to the user (unknown group => the raw value, so a plugin's own
    // section name still renders rather than showing a missing-key placeholder).
    private void Regroup(IEnumerable<ConnectionFieldInput> fields, ObservableCollection<ConnectionFieldGroup> target)
    {
        foreach (var group in fields.GroupBy(f => f.Field.Group))
        {
            target.Add(new ConnectionFieldGroup(GroupTitle(group.Key), group.ToList()));
        }
    }

    private string? GroupTitle(string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return null;
        }

        var key = $"FieldGroup{group.Replace(" ", string.Empty)}";
        var translated = Loc[key];
        return translated == key ? group : translated;
    }

    // Route B: a provider may render the Advanced section itself. Its view reads/writes the same declared
    // field values through the context, so save/import/BuildConnectionString are unaffected. The view
    // reads the current field values once at construction, so this must run AFTER any prefill (see
    // LoadForEdit) — otherwise an edited connection's stored advanced values show as defaults.
    private void BuildCustomAdvancedView(IDbProvider provider)
    {
        CustomAdvancedView = provider is ICustomConnectionUi customUi
            ? customUi.CreateAdvancedView(new FieldValuesContext(this))
            : null;
        HasCustomAdvancedView = CustomAdvancedView is not null;
        HasAdvancedFields = AdvancedFields.Count > 0 || HasCustomAdvancedView;
    }

    private IReadOnlyDictionary<string, string?>? TryParse(string providerId, string connectionString)
    {
        try
        {
            return _providers.Get(providerId).ParseConnectionString(connectionString);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Prefill the fields from the pasted connection string (import flow).</summary>
    [RelayCommand]
    private void ImportFromConnectionString()
    {
        if (SelectedProvider is not { } option || string.IsNullOrWhiteSpace(ImportConnectionString))
        {
            return;
        }

        IReadOnlyDictionary<string, string?>? parsed;
        try
        {
            parsed = _providers.Get(option.Id).ParseConnectionString(ImportConnectionString);
        }
        catch (Exception ex)
        {
            TestResult = ex.Message;
            return;
        }

        if (parsed is null)
        {
            TestResult = Loc["ImportNotSupported"];
            return;
        }

        foreach (var field in Fields)
        {
            if (parsed.TryGetValue(field.Field.Key, out var value))
            {
                field.Value = value;
            }
        }

        // Reveal Advanced if the paste populated anything hidden, so the user sees what was imported.
        if (AdvancedFields.Any(f => parsed.ContainsKey(f.Field.Key)))
        {
            IsAdvancedExpanded = true;
        }

        TestResult = Loc["ImportDone"];
    }

    // A field's value changed -> the required-fields check may flip, so re-evaluate the Save gate. The
    // header line quotes host/port/user, so it follows along (SE-287).
    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionFieldInput.Value))
        {
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(HeaderSubtitle));
        }
    }

    private Dictionary<string, string?> Values() =>
        Fields.ToDictionary(f => f.Field.Key, f => f.Value);

    [RelayCommand]
    private async Task TestAsync(CancellationToken ct)
    {
        if (SelectedProvider is not { } option)
        {
            return;
        }

        try
        {
            var profile = _connections.BuildProfile(Name, option.Id, Values());
            var ok = await _providers.Get(option.Id).TestConnectionAsync(profile, ct);
            TestResult = ok ? Loc["TestOk"] : Loc["TestFailed"];
            TestOk = ok;
        }
        catch (Exception ex)
        {
            TestResult = ex.Message;
            TestOk = false;
        }
    }

    /// <summary>Persist and return the saved connection (secrets go to the keychain).</summary>
    public SavedConnection Save() =>
        _connections.Save(_id, Name, SelectedProvider!.Id, Values(), Color, ReadOnly, Folder, AiAccess, ExcludeFromMcp, origin: _origin);

    /// <summary>Bridges a provider's custom advanced view (Route B) to the dialog's field inputs by key,
    /// so its edits land in the same values the host saves and passes to BuildConnectionString.</summary>
    private sealed class FieldValuesContext(ConnectionDialogViewModel owner) : IConnectionUiContext
    {
        public string? GetValue(string key) =>
            owner.Fields.FirstOrDefault(f => f.Field.Key == key)?.Value;

        public void SetValue(string key, string? value)
        {
            if (owner.Fields.FirstOrDefault(f => f.Field.Key == key) is { } field)
            {
                field.Value = value;
            }
        }
    }
}

/// <summary>One titled block of connection fields (SE-287). <paramref name="Title"/> is null for the
/// provider's ungrouped fields, which render straight into the section without a sub-heading.</summary>
public sealed record ConnectionFieldGroup(string? Title, IReadOnlyList<ConnectionFieldInput> Fields)
{
    public bool HasTitle => Title is not null;
}

/// <summary>A selectable provider in the connection dialog: the manifest id plus its friendly label.</summary>
public sealed record ProviderOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>One colour choice in the connection form's colour dropdown (<see cref="Value"/> null = none).</summary>
public sealed class ColorSwatch(string? value, string name)
{
    public string? Value { get; } = value;

    /// <summary>Localised, speakable name ("Red", "No colour") — SE-287: a swatch without one is a
    /// coloured dot you cannot describe, and "none" reads as an empty circle.</summary>
    public string Name { get; } = name;

    /// <summary>True for the "no colour" entry, which draws a placeholder instead of a filled chip.</summary>
    public bool HasColor => Value is not null;
}
