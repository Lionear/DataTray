using DataTray.Extensions.QueryBuilder.Dialects;

namespace DataTray.Extensions.QueryBuilder.Tests.Dialects;

public class DialectLiteralTests
{
    [Theory]
    [InlineData("sqlserver", "a]b", "[a]]b]")]
    [InlineData("postgres", "a\"b", "\"a\"\"b\"")]
    [InlineData("mysql", "a`b", "`a``b`")]
    [InlineData("sqlite", "a\"b", "\"a\"\"b\"")]
    public void QuoteIdentifier_EscapesTheQuoteChar(string provider, string name, string expected)
    {
        Assert.Equal(expected, SqlDialects.For(provider, null).QuoteIdentifier(name));
    }

    [Theory]
    [InlineData("sqlserver", "it's", "N'it''s'")]
    [InlineData("postgres", "it's\\x", "'it''s\\x'")]
    [InlineData("mysql", "it's\\x", "'it''s\\\\x'")]
    [InlineData("sqlite", "it's\\x", "'it''s\\x'")]
    public void StringLiteral_Escapes(string provider, string value, string expected)
    {
        Assert.Equal(expected, SqlDialects.For(provider, null).StringLiteral(value));
    }

    [Theory]
    [InlineData("sqlserver", null, true, "1")]
    [InlineData("sqlserver", null, false, "0")]
    [InlineData("postgres", null, true, "TRUE")]
    [InlineData("sqlite", "3.22.0", true, "1")]
    [InlineData("sqlite", "3.23.0", false, "FALSE")]
    public void BooleanLiteral_FollowsTheDialect(string provider, string? version, bool value, string expected)
    {
        Assert.Equal(expected, SqlDialects.For(provider, version).BooleanLiteral(value));
    }

    [Fact]
    public void For_RejectsAnUnknownProvider()
    {
        Assert.Throws<NotSupportedException>(() => SqlDialects.For("oracle", null));
    }
}
