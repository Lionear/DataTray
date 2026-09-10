using System.Globalization;
using DataTray.App.Localization;

namespace DataTray.App.Tests;

/// <summary>
/// SE-276: the UI-language dropdown must not drag the machine's date/number format along with it.
/// Result-grid cells are rendered by Avalonia's default object-to-string conversion, which uses
/// <see cref="CultureInfo.CurrentCulture"/> — so assigning that here turned every timestamp American.
/// </summary>
public class LocalizerCultureTests
{
    [Fact]
    public void SetCulture_SwitchesUiLanguageButLeavesFormattingOnTheSystemCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");

            new ResxLocalizer().SetCulture(CultureInfo.GetCultureInfo("en"));

            Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("nl-NL", CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }
}
