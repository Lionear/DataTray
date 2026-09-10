namespace DataTray.Core.Export;

/// <summary>
/// How <see cref="ResultExporter.ToHtml"/> dresses the table it produces — for "Copy as HTML" and the HTML
/// file export alike. The styling is inline per cell, since the rich-text targets this is aimed at (Outlook,
/// Word) render with Word's engine and ignore a stylesheet.
/// </summary>
public enum HtmlTableStyle
{
    /// <summary>A bare <c>&lt;table&gt;</c> with no attributes — the target decides how it looks.</summary>
    Plain,

    /// <summary>Lines only: an accent rule under the headers and a hairline between rows. Unobtrusive in a
    /// mail or document, and prints clean.</summary>
    Hairlines,

    /// <summary>Header row filled with the brand colour, cells in a grid. The familiar look, and the header
    /// stays readable where a long table breaks across a page.</summary>
    HeaderFill,

    /// <summary>Filled header plus a tint on every other row — worth its extra markup on wide results.</summary>
    HeaderFillZebra
}
