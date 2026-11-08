using System.Globalization;
using DataTray.App.Converters;

namespace DataTray.App.Tests;

public class StringFormatConverterTests
{
    [Fact]
    public void Convert_FormatsTheFirstValueWithTheRest()
    {
        var text = StringFormatConverter.Instance.Convert(
            ["Written by {0} · read-only while linked", "Builder"], typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal("Written by Builder · read-only while linked", text);
    }
}
