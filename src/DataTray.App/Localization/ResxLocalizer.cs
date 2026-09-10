using System.ComponentModel;
using System.Globalization;
using System.Resources;
using DataTray.Core.Localization;

namespace DataTray.App.Localization;

public sealed class ResxLocalizer : ILocalizer
{
    private readonly ResourceManager _resources =
        new("DataTray.App.Resources.Strings", typeof(ResxLocalizer).Assembly);

    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo Culture => _culture;

    public string this[string key] => _resources.GetString(key, _culture) ?? key;

    public string Get(string key, params object[] args)
    {
        var format = this[key];
        // Wording from the UI language, the values inside it in the machine's format — same split as
        // SetCulture below.
        return args.Length == 0 ? format : string.Format(CultureInfo.CurrentCulture, format, args);
    }

    public void SetCulture(CultureInfo culture)
    {
        // UI language only. CurrentCulture is deliberately left on the OS setting so dates and numbers
        // keep the machine's format: assigning it here made picking English render every result-grid
        // timestamp as "12/2/2025 11:10:40 AM" regardless of where the user is (SE-276).
        _culture = culture;
        CultureInfo.CurrentUICulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        // Explicit indexer-changed notification (WPF/Avalonia convention) — belt-and-suspenders
        // alongside the null-name "everything changed" signal above, in case Avalonia's binding
        // engine specifically expects this form for an indexer access node.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
