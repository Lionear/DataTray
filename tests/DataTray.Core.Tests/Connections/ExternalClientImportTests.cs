using DataTray.Core.Connections.Import;

namespace DataTray.Core.Tests.Connections;

// SE-237: the clients that store plain fields or a connection string instead of a JDBC URL. Each reader
// only has to reach the canonical concepts — the translation to provider field keys is shared with SE-233
// and guarded there — so these tests are about each file format's own quirks, and about the standing rule
// that no password is ever carried over.
public class ExternalClientImportTests
{
    private static IReadOnlyList<string>? Fields(string providerId) => providerId switch
    {
        "postgres" or "mysql" or "sqlserver" or "mongodb" =>
            ["host", "port", "database", "username", "password"],
        _ => null
    };

    [Fact]
    public void ReadsEveryServiceInAPgServiceFile()
    {
        const string text = """
            # comments and blank lines are skipped

            [reporting]
            host=db.internal
            port=6432
            dbname=orders
            user=reporting
            password=hunter2

            [local]
            host=localhost
            dbname=dev
            """;

        var found = ExternalConnectionImport.FromPgService(text, Fields);

        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.Equal("postgres", f.ProviderId));

        Assert.Equal("reporting", found[0].Name);
        Assert.Equal("db.internal", found[0].Values["host"]);
        Assert.Equal("6432", found[0].Values["port"]);
        Assert.Equal("orders", found[0].Values["database"]);
        Assert.Equal("reporting", found[0].Values["username"]);

        // Plain text in the user's own file, so it comes along — and is flagged, since not every client
        // gives one up.
        Assert.Equal("hunter2", found[0].Values["password"]);
        Assert.True(found[0].HasPassword);

        // The last section still lands even though no section header follows it, and it has no password.
        Assert.Equal("local", found[1].Name);
        Assert.Equal("dev", found[1].Values["database"]);
        Assert.False(found[1].HasPassword);
    }

    [Fact]
    public void APasswordIsDroppedWhenTheProviderHasNoFieldForOne()
    {
        const string text = """
            [notes]
            host=localhost
            dbname=app
            password=hunter2
            """;

        var found = Assert.Single(ExternalConnectionImport.FromPgService(
            text, _ => ["host", "database"]));

        Assert.DoesNotContain(found.Values, v => v.Value == "hunter2");
        Assert.False(found.HasPassword);
    }

    [Fact]
    public void ReadsAWorkbenchConnection()
    {
        const string xml = """
            <data grt_format="2.0">
              <value type="list" content-type="object">
                <value type="object" struct-name="db.mgmt.Connection" id="1">
                  <value type="string" key="name">Local instance</value>
                  <value type="dict" key="parameterValues">
                    <value type="string" key="hostName">127.0.0.1</value>
                    <value type="int" key="port">3307</value>
                    <value type="string" key="userName">root</value>
                    <value type="string" key="schema">shop</value>
                  </value>
                </value>
              </value>
            </data>
            """;

        var found = Assert.Single(ExternalConnectionImport.FromMySqlWorkbench(xml, Fields));

        Assert.True(found.CanImport);
        Assert.Equal("Workbench", found.Source);
        Assert.Equal("Local instance", found.Name);
        Assert.Equal("mysql", found.ProviderId);
        Assert.Equal("127.0.0.1", found.Values["host"]);
        Assert.Equal("3307", found.Values["port"]);
        Assert.Equal("shop", found.Values["database"]);
        Assert.Equal("root", found.Values["username"]);
    }

    [Theory]
    [InlineData("sql01", "sql01", null)]
    [InlineData("sql01,1435", "sql01", "1435")]
    [InlineData("tcp:sql01,1435", "sql01", "1435")]
    [InlineData("sql01\\SQLEXPRESS", "sql01", null)]
    public void SplitsTheManyShapesOfASqlServerServerString(string server, string host, string? port)
    {
        var json = $$"""
            { "mssql.connections": [ { "server": "{{server.Replace("\\", "\\\\")}}", "database": "Sales" } ] }
            """;

        var found = Assert.Single(ExternalConnectionImport.FromMssqlSettings(json, Fields));

        Assert.Equal("sqlserver", found.ProviderId);
        Assert.Equal(host, found.Values["host"]);
        Assert.Equal(port, found.Values.TryGetValue("port", out var actual) ? actual : null);
    }

    [Fact]
    public void ReadsMssqlProfilesFromASettingsFileWithCommentsAndTrailingCommas()
    {
        const string json = """
            {
              // the editor's own settings sit alongside; only mssql.connections is read
              "editor.fontSize": 13,
              "mssql.connections": [
                {
                  "server": "sql01,1433",
                  "database": "Sales",
                  "authenticationType": "SqlLogin",
                  "user": "reporting",
                  "password": "hunter2",
                  "profileName": "Sales reporting",
                },
              ],
            }
            """;

        var found = Assert.Single(ExternalConnectionImport.FromMssqlSettings(json, Fields));

        Assert.Equal("Sales reporting", found.Name);
        Assert.Equal("sql01", found.Values["host"]);
        Assert.Equal("1433", found.Values["port"]);
        Assert.Equal("reporting", found.Values["username"]);

        // A password only appears here when the user declined the editor's credential store; then it is
        // plain text and comes along.
        Assert.Equal("hunter2", found.Values["password"]);
        Assert.True(found.HasPassword);
    }

    [Fact]
    public void ASettingsFileWithoutMssqlConnectionsYieldsNothing()
    {
        Assert.Empty(ExternalConnectionImport.FromMssqlSettings("""{ "editor.fontSize": 13 }""", Fields));
    }

    // Compass keeps the user in the URI and the password in a separate encrypted connectionSecrets blob —
    // which is another application's secret store, so it stays shut. Verified against a real Compass file:
    // its connectionString carries userinfo without a password.
    [Fact]
    public void ReadsACompassConnectionAndLeavesItsEncryptedSecretsShut()
    {
        const string json = """
            {
              "_id": "db3e7bbf",
              "connectionInfo": {
                "id": "db3e7bbf",
                "connectionOptions": { "connectionString": "mongodb://root@mongo01:27018/events" },
                "favorite": { "name": "Events" }
              },
              "connectionSecrets": "djExZ/lFUKKMN2wSd82Wbm=="
            }
            """;

        var found = Assert.Single(ExternalConnectionImport.FromCompass(json, Fields));

        Assert.True(found.CanImport);
        Assert.Equal("Compass", found.Source);
        Assert.Equal("Events", found.Name);
        Assert.Equal("mongodb", found.ProviderId);
        Assert.Equal("mongo01", found.Values["host"]);
        Assert.Equal("27018", found.Values["port"]);
        Assert.Equal("events", found.Values["database"]);
        Assert.Equal("root", found.Values["username"]);
        Assert.False(found.HasPassword);
        Assert.DoesNotContain(found.Values, v => v.Value?.Contains("djExZ", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void AnAtlasSrvUriHasNoPortToImport()
    {
        const string json = """
            {
              "connectionInfo": {
                "id": "atlas-1",
                "connectionOptions": { "connectionString": "mongodb+srv://cluster0.abcde.mongodb.net/prod" }
              }
            }
            """;

        var found = Assert.Single(ExternalConnectionImport.FromCompass(json, Fields));

        Assert.Equal("mongodb", found.ProviderId);
        Assert.Equal("cluster0.abcde.mongodb.net", found.Values["host"]);
        Assert.Equal("prod", found.Values["database"]);
        Assert.DoesNotContain("port", found.Values.Keys);
        // No favourite name saved, so the connection falls back to its Compass id rather than "(unnamed)".
        Assert.Equal("atlas-1", found.Name);
    }

    [Fact]
    public void AClientWhoseProviderIsMissingIsReportedRatherThanDropped()
    {
        const string text = """
            [cache]
            host=localhost
            dbname=app
            """;

        var found = Assert.Single(ExternalConnectionImport.FromPgService(text, _ => null));

        Assert.False(found.CanImport);
        Assert.Contains("not installed", found.SkipReason);
    }
}
