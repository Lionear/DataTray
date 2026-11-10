using DataTray.Extensions.QueryBuilder.Model;
using DataTray.Extensions.QueryBuilder.Reading;

namespace DataTray.Extensions.QueryBuilder.Tests;

// ReadBack.ReadAndVerify is the only public read path; the readers and their helpers stay inside the plugin.
public class PublicApiTests
{
    [Theory]
    [InlineData(typeof(SqlReaders))]
    [InlineData(typeof(ISqlReader))]
    [InlineData(typeof(SqlTokens))]
    [InlineData(typeof(ValueListConverter<>))]
    [InlineData(typeof(ValueListConverterFactory))]
    public void Type_IsNotPublic(Type type)
    {
        Assert.False(type.IsVisible);
    }
}
