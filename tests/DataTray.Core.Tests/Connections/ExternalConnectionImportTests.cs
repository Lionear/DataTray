using DataTray.Core.Connections.Import;

namespace DataTray.Core.Tests.Connections;

// SE-233: reading connections out of DataGrip/DBeaver. The rules worth guarding are that every engine's
// JDBC URL shape lands in the same canonical fields, that a connection we can't map is reported instead
// of dropped, and that no password ever comes along.
public class ExternalConnectionImportTests
{
    // The four built-in providers' declared field keys; "mongodb" stands in for a provider that isn't installed.
    private static IReadOnlyList<string>? Fields(string providerId) => providerId switch
    {
        "postgres" or "mysql" or "sqlserver" => ["host", "port", "database", "username", "password"],
        "sqlite" => ["path"],
        _ => null
    };

    [Theory]
    [InlineData("jdbc:postgresql://db.internal:6432/orders", "postgresql", "db.internal", "6432", "orders")]
    [InlineData("jdbc:mysql://localhost:3306/shop?useSSL=false", "mysql", "localhost", "3306", "shop")]
    [InlineData("jdbc:sqlserver://sql01:1433;databaseName=Sales;encrypt=true", "sqlserver", "sql01", "1433", "Sales")]
    [InlineData("jdbc:postgresql://only-host/appdb", "postgresql", "only-host", null, "appdb")]
    public void ParsesTheUrlShapesEachEngineUses(
        string url, string subprotocol, string host, string? port, string database)
    {
        var (parsed, values) = ExternalConnectionImport.ParseJdbcUrl(url);

        Assert.Equal(subprotocol, parsed);
        Assert.Equal(host, values["host"]);
        Assert.Equal(database, values["database"]);
        Assert.Equal(port, values.TryGetValue("port", out var actual) ? actual : null);
    }

    [Fact]
    public void FileBackedEnginesKeepTheirPath()
    {
        var (subprotocol, values) = ExternalConnectionImport.ParseJdbcUrl("jdbc:sqlite:/home/rick/app.db");

        Assert.Equal("sqlite", subprotocol);
        Assert.Equal("/home/rick/app.db", values["path"]);
    }

    [Fact]
    public void InlineCredentialsYieldTheUserButNeverThePassword()
    {
        var (_, values) = ExternalConnectionImport.ParseJdbcUrl("jdbc:postgresql://rick:hunter2@db:5432/app");

        Assert.Equal("db", values["host"]);
        Assert.Equal("rick", values["username"]);
        Assert.DoesNotContain(values, v => v.Value == "hunter2");
    }

    [Fact]
    public void ReadsADataGripDataSource()
    {
        const string xml = """
            <component name="DataSourceManagerImpl">
              <data-source name="orders@prod" uuid="a1">
                <driver-ref>postgresql</driver-ref>
                <jdbc-url>jdbc:postgresql://prod-db:5432/orders</jdbc-url>
                <user-name>reporting</user-name>
              </data-source>
            </component>
            """;

        var found = Assert.Single(ExternalConnectionImport.FromDataGrip(xml, Fields));

        Assert.True(found.CanImport);
        Assert.Equal("DataGrip", found.Source);
        Assert.Equal("orders@prod", found.Name);
        Assert.Equal("postgres", found.ProviderId);
        Assert.Equal("prod-db", found.Values["host"]);
        Assert.Equal("5432", found.Values["port"]);
        Assert.Equal("orders", found.Values["database"]);
        Assert.Equal("reporting", found.Values["username"]);
        Assert.DoesNotContain("password", found.Values.Keys);
    }

    [Fact]
    public void ReadsADBeaverDataSourceIncludingItsFolder()
    {
        const string json = """
            {
              "connections": {
                "postgres-jdbc-1": {
                  "provider": "postgresql",
                  "name": "Local Postgres",
                  "folder": "Development",
                  "save-password": true,
                  "configuration": {
                    "host": "127.0.0.1",
                    "port": "5433",
                    "database": "app",
                    "url": "jdbc:postgresql://localhost:5432/stale",
                    "user": "postgres"
                  }
                }
              }
            }
            """;

        var found = Assert.Single(ExternalConnectionImport.FromDBeaver(json, Fields));

        Assert.True(found.CanImport);
        Assert.Equal("Local Postgres", found.Name);
        Assert.Equal("Development", found.Folder);
        // The explicit fields win over a URL the user left stale.
        Assert.Equal("127.0.0.1", found.Values["host"]);
        Assert.Equal("5433", found.Values["port"]);
        Assert.Equal("app", found.Values["database"]);
        Assert.Equal("postgres", found.Values["username"]);
    }

    [Fact]
    public void SqliteLandsOnThePathFieldItsProviderDeclares()
    {
        const string json = """
            {
              "connections": {
                "sqlite-1": {
                  "name": "Notes",
                  "configuration": { "url": "jdbc:sqlite:/home/rick/notes.db" }
                }
              }
            }
            """;

        var found = Assert.Single(ExternalConnectionImport.FromDBeaver(json, Fields));

        Assert.Equal("sqlite", found.ProviderId);
        Assert.Equal("/home/rick/notes.db", found.Values["path"]);
    }

    [Fact]
    public void AnEngineWithNoProviderIsReportedRatherThanDropped()
    {
        const string xml = """
            <component>
              <data-source name="cache"><jdbc-url>jdbc:informix://host:1526/db</jdbc-url></data-source>
              <data-source name="no url"><driver-ref>postgresql</driver-ref></data-source>
              <data-source name="mongo"><jdbc-url>jdbc:mongodb://host:27017/logs</jdbc-url></data-source>
            </component>
            """;

        var found = ExternalConnectionImport.FromDataGrip(xml, Fields);

        Assert.Equal(3, found.Count);
        Assert.All(found, f => Assert.False(f.CanImport));
        Assert.Contains("informix", found[0].SkipReason);
        Assert.Contains("JDBC URL", found[1].SkipReason);
        // Known engine, but its provider plugin isn't installed in this build.
        Assert.Contains("not installed", found[2].SkipReason);
    }

    [Fact]
    public void MalformedConfigsYieldNothingInsteadOfThrowing()
    {
        Assert.Empty(ExternalConnectionImport.FromDBeaver("{}", Fields));
        Assert.Empty(ExternalConnectionImport.FromDataGrip("<component />", Fields));
    }
}
