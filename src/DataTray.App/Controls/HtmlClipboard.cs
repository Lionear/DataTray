using System;
using System.Text;
using Avalonia.Input;

namespace DataTray.App.Controls;

/// <summary>
/// The clipboard side of "Copy as HTML": which native format the markup goes on, and the bytes that format
/// expects. A paste target picks by format name, not by content — HTML written as plain text lands in Outlook
/// or Word as plain text, however table-shaped it looks.
/// </summary>
public static class HtmlClipboard
{
    private const string FragmentPrefix = "<html><body><!--StartFragment-->";
    private const string FragmentSuffix = "<!--EndFragment--></body></html>";

    /// <summary>The native HTML format of the running platform. Avalonia passes a platform format name
    /// through untouched, so these are the system's own names: a registered clipboard format on Windows,
    /// a UTI on macOS, a MIME type on X11/Wayland.</summary>
    public static DataFormat<byte[]> Format { get; } = DataFormat.CreateBytesPlatformFormat(
        OperatingSystem.IsWindows() ? "HTML Format"
            : OperatingSystem.IsMacOS() ? "public.html"
            : "text/html");

    /// <summary>The payload for <see cref="Format"/>: UTF-8, wrapped in a CF_HTML envelope on Windows.</summary>
    public static byte[] ToPayload(string html)
        => OperatingSystem.IsWindows() ? ToCfHtml(html) : Encoding.UTF8.GetBytes(html);

    /// <summary>
    /// Windows' CF_HTML: an ASCII header whose four offsets into the UTF-8 stream delimit the document and the
    /// pasted fragment. Outlook drops the whole clipboard entry when they're wrong rather than complaining, so
    /// they're counted in bytes (not chars) and every number is a fixed ten digits — a constant header length
    /// is what lets us measure the header before we know what to put in it.
    /// </summary>
    public static byte[] ToCfHtml(string html)
    {
        static string Header(int startHtml, int endHtml, int startFragment, int endFragment)
            => $"Version:0.9\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\n"
                + $"StartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n";

        var headerLength = Encoding.UTF8.GetByteCount(Header(0, 0, 0, 0));
        var fragmentStart = headerLength + Encoding.UTF8.GetByteCount(FragmentPrefix);
        var fragmentEnd = fragmentStart + Encoding.UTF8.GetByteCount(html);
        var endHtml = fragmentEnd + Encoding.UTF8.GetByteCount(FragmentSuffix);

        return Encoding.UTF8.GetBytes(
            Header(headerLength, endHtml, fragmentStart, fragmentEnd) + FragmentPrefix + html + FragmentSuffix);
    }
}
