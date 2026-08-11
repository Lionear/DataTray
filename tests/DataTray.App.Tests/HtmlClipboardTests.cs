using System.Text;
using System.Text.RegularExpressions;
using DataTray.App.Controls;

namespace DataTray.App.Tests;

/// <summary>
/// CF_HTML's offsets are the part that silently fails: get them wrong and Outlook ignores the clipboard entry
/// without an error. The fragment here holds a multi-byte character, so a char-counted offset would land in the
/// wrong place and fail these assertions.
/// </summary>
public class HtmlClipboardTests
{
    private const string Fragment = "<table><thead><tr><th>café</th></tr></thead></table>";

    [Fact]
    public void CfHtmlOffsetsAreByteOffsetsIntoItsOwnStream()
    {
        var bytes = HtmlClipboard.ToCfHtml(Fragment);
        var header = Encoding.UTF8.GetString(bytes);

        int Offset(string name) => int.Parse(Regex.Match(header, $@"\b{name}:(\d{{10}})\r\n").Groups[1].Value);

        var startHtml = Offset("StartHTML");
        var startFragment = Offset("StartFragment");
        var endFragment = Offset("EndFragment");

        Assert.StartsWith("Version:0.9\r\n", header);
        Assert.Equal("<html>", Encoding.UTF8.GetString(bytes, startHtml, 6));
        Assert.Equal(bytes.Length, Offset("EndHTML"));
        Assert.Equal(Fragment, Encoding.UTF8.GetString(bytes, startFragment, endFragment - startFragment));
    }
}
