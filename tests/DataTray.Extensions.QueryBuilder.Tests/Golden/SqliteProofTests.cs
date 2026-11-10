using Microsoft.Data.Sqlite;

namespace DataTray.Extensions.QueryBuilder.Tests.Golden;

public class SqliteProofTests
{
    public static TheoryData<string, string> Cases() => FixtureCases.SqlCases("sqlite");

    [Fact]
    public void Versions_SqliteMatchesTheBundledLibrary()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";

        Assert.Equal(FixtureCases.Version("sqlite"), command.ExecuteScalar());
    }

    [Fact]
    public void Explain_UnknownColumn_Throws()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN SELECT \"NoSuchColumn\" FROM \"Researcher\"";

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Theory, MemberData(nameof(Cases))]
    public void Golden_ExplainsAgainstTheDemoSchema(string dialect, string name)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN " + FixtureCases.Sql(name, dialect);

        var exception = Record.Exception(() => command.ExecuteNonQuery());

        Assert.Null(exception);
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = FixtureCases.SchemaSql;
        schema.ExecuteNonQuery();
        return connection;
    }
}
