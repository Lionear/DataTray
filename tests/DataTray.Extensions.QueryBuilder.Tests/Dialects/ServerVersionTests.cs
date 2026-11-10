using DataTray.Extensions.QueryBuilder.Dialects;

namespace DataTray.Extensions.QueryBuilder.Tests.Dialects;

public class ServerVersionTests
{
    [Theory]
    [InlineData("8.0.31-log", 8, 0, 31)]
    [InlineData("PostgreSQL 16.4 on x86_64-pc-linux-gnu, compiled by gcc", 16, 4, 0)]
    [InlineData("3.45.1", 3, 45, 1)]
    [InlineData("16.00.1000", 16, 0, 1000)]
    [InlineData("16", 16, 0, 0)]
    [InlineData("16.4 (Debian 16.4-1.pgdg120+1)", 16, 4, 0)]
    public void ServerVersion_Parses(string text, int major, int minor, int patch)
    {
        Assert.Equal(new ServerVersion(major, minor, patch), ServerVersion.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("99999999999.1")]
    public void ServerVersion_ReturnsNullWhenUnparseable(string? text)
    {
        Assert.Null(ServerVersion.Parse(text));
    }
}
