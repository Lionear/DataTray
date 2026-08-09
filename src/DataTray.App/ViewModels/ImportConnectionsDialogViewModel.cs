using System.Collections.ObjectModel;
using DataTray.Core.Connections.Import;
using DataTray.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DataTray.App.ViewModels;

/// <summary>One row in the import picker: a discovered connection plus its tick box. Rows that can't be
/// imported are still listed (greyed, with the reason) so a partial import is visible.</summary>
public sealed partial class ImportConnectionRow(DiscoveredConnection connection, string detail) : ObservableObject
{
    public DiscoveredConnection Connection { get; } = connection;

    public string Name => Connection.Name;

    public string Source => Connection.Source;

    /// <summary>"postgres · db.internal:6432/orders", or the reason this one is skipped.</summary>
    public string Detail { get; } = detail;

    public bool CanImport => Connection.CanImport;

    [ObservableProperty]
    private bool _isSelected = connection.CanImport;
}

/// <summary>
/// Backs the "import from DataGrip/DBeaver" picker (SE-233). Discovery and parsing live in
/// <see cref="ExternalConnectionImport"/>; this only turns the result into rows and hands the ticked
/// ones back — the Connection Manager does the saving.
/// </summary>
public partial class ImportConnectionsDialogViewModel : ViewModelBase
{
    public ImportConnectionsDialogViewModel(ILocalizer localizer)
    {
        Loc = localizer;
    }

    public ILocalizer Loc { get; }

    public ObservableCollection<ImportConnectionRow> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public bool IsEmpty => Rows.Count == 0;

    public string Summary => Loc.Get("ImportConnectionsFound", Rows.Count(r => r.CanImport), Rows.Count);

    public void Configure(IReadOnlyList<DiscoveredConnection> found)
    {
        Rows.Clear();
        foreach (var connection in found.OrderBy(c => c.Source, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Rows.Add(new ImportConnectionRow(connection, Describe(connection)));
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>The ticked, importable rows — what the caller should save.</summary>
    public IReadOnlyList<DiscoveredConnection> Selected =>
        Rows.Where(r => r is { IsSelected: true, CanImport: true }).Select(r => r.Connection).ToList();

    [RelayCommand]
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void SelectNone() => SetAll(false);

    private void SetAll(bool selected)
    {
        foreach (var row in Rows.Where(r => r.CanImport))
        {
            row.IsSelected = selected;
        }
    }

    private static string Describe(DiscoveredConnection connection)
    {
        if (connection.SkipReason is { } reason)
        {
            return reason;
        }

        var values = connection.Values;
        var target = values.TryGetValue("path", out var path) && !string.IsNullOrWhiteSpace(path)
            ? path
            : Endpoint(values);

        return string.IsNullOrWhiteSpace(target) ? connection.ProviderId! : $"{connection.ProviderId} · {target}";
    }

    private static string Endpoint(IReadOnlyDictionary<string, string?> values)
    {
        var host = values.GetValueOrDefault("host");
        var port = values.GetValueOrDefault("port");
        var database = values.GetValueOrDefault("database");

        var endpoint = string.IsNullOrWhiteSpace(port) ? host : $"{host}:{port}";
        return string.IsNullOrWhiteSpace(database) ? endpoint ?? string.Empty : $"{endpoint}/{database}";
    }
}
