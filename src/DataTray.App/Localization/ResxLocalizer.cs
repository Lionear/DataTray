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
        return args.Length == 0 ? format : string.Format(_culture, format, args);
    }

    public void SetCulture(CultureInfo culture)
    {
        _culture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        // Explicit indexer-changed notification (WPF/Avalonia convention) — belt-and-suspenders
        // alongside the null-name "everything changed" signal above, in case Avalonia's binding
        // engine specifically expects this form for an indexer access node.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
