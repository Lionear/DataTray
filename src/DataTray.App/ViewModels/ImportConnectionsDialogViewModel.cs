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

    /// <summary>
    /// Fetches the passwords that live in the OS credential store instead of the client's own file, and
    /// returns the same rows with those folded in (SE-238). Set by the owner, which knows the providers;
    /// null leaves the offer hidden.
    /// </summary>
    public Func<IReadOnlyList<DiscoveredConnection>, IReadOnlyList<DiscoveredConnection>>? FetchPasswordsRequested { get; set; }

    /// <summary>Whether any row has a password worth asking the OS for — the offer stays hidden otherwise
    /// rather than being a button that does nothing.</summary>
    public bool CanFetchPasswords =>
        FetchPasswordsRequested is not null && Rows.Any(r => r.Connection.HasFetchableSecret);

    /// <summary>How many rows this could still fetch a password for.</summary>
    public string FetchPasswordsLabel =>
        Loc.Get("ImportFetchPasswords", Rows.Count(r => r.Connection.HasFetchableSecret));

    /// <summary>
    /// Ask the OS for the passwords it holds. Opt-in and explicit: nothing here runs until the user presses
    /// it, and the OS decides whether to hand anything over — it may prompt, and it may refuse.
    /// </summary>
    [RelayCommand]
    private void FetchPasswords()
    {
        if (FetchPasswordsRequested is not { } fetch)
        {
            return;
        }

        // Keep what the user had ticked: re-Configure rebuilds every row, and silently re-selecting
        // everything would undo their choices.
        var deselected = Rows.Where(r => !r.IsSelected).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

        Configure(fetch(Rows.Select(r => r.Connection).ToList()));

        foreach (var row in Rows.Where(r => deselected.Contains(r.Name)))
        {
            row.IsSelected = false;
        }
    }

    public void Configure(IReadOnlyList<DiscoveredConnection> found)
    {
        Rows.Clear();
        foreach (var connection in found.OrderBy(c => c.Source, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            // Whether a password came along differs per client, so it is said per row rather than once in
            // the header — a silent difference is the kind that only shows up on first connect.
            var detail = Describe(connection);
            if (connection.HasPassword)
            {
                detail = $"{detail} · {Loc["ImportConnectionsWithPassword"]}";
            }

            Rows.Add(new ImportConnectionRow(connection, detail));
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanFetchPasswords));
        OnPropertyChanged(nameof(FetchPasswordsLabel));
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
