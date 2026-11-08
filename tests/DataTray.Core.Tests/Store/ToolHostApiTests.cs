using DataTray.Sdk.Tools;

namespace DataTray.Core.Tests.Store;

public class ToolHostApiTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public void IsCompatible_Version_MatchesV9Window(int version, bool expected)
    {
        Assert.Equal(expected, ToolHostApi.IsCompatible(version));
    }
}
