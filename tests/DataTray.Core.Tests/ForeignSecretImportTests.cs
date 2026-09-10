using DataTray.Core.Connections.Import;

namespace DataTray.Core.Tests;

/// <summary>
/// SE-238: passwords that live in the OS credential store rather than the client's own file.
/// </summary>
public class ForeignSecretImportTests
{
    // The shape a real DataGrip dataSources.xml has, cut down to what the importer reads. The uuids are the
    // ones observed while verifying the key derivation against a live profile.
    private const string DataGripXml = """
        <project version="4">
          <component name="DataSourceManagerImpl">
            <data-source source="LOCAL" name="orders@prod" uuid="56ef87d9-f24e-4f97-9d95-6d4babecdad6">
              <jdbc-url>jdbc:postgresql://prod-db:5432/orders</jdbc-url>
              <user-name>app_reader</user-name>
            </data-source>
            <data-source source="LOCAL" name="no password here" uuid="8a41184c-7a2f-4ee0-a0b4-26556ffa31b1">
              <jdbc-url>jdbc:postgresql://other-db:5432/spare</jdbc-url>
              <user-name>someone</user-name>
            </data-source>
          </component>
        </project>
        """;

    [Fact] // The whole gate of SE-238: the keychain service name is derived from the data-source uuid.
           // Verified against a real DataGrip profile on Fedora 44 + KWallet; this pins the format so a
           // stray edit to the separator can't silently stop finding every password.
    public void The_keychain_service_name_is_derived_from_the_datasource_uuid()
    {
        Assert.Equal(
            "IntelliJ Platform DB — 56ef87d9-f24e-4f97-9d95-6d4babecdad6",
            ExternalConnectionImport.SecretServiceName("DataGrip", "56ef87d9-f24e-4f97-9d95-6d4babecdad6"));

        // A client we have no credential-store convention for must not be guessed at.
        Assert.Null(ExternalConnectionImport.SecretServiceName("DBeaver", "whatever"));
    }

    [Fact]
    public void Discovery_records_the_uuid_but_fetches_nothing_by_itself()
    {
        var found = ExternalConnectionImport.FromDataGrip(DataGripXml, PostgresFields);

        var row = found.Single(c => c.Name == "orders@prod");
        Assert.Equal("56ef87d9-f24e-4f97-9d95-6d4babecdad6", row.SecretRef);
        // Reading config files never touches a credential store — that is the opt-in.
        Assert.False(row.HasPassword);
        Assert.True(row.HasFetchableSecret);
    }

    [Fact]
    public void A_fetched_password_lands_under_the_provider_own_field_key()
    {
        var found = ExternalConnectionImport.FromDataGrip(DataGripXml, PostgresFields);
        var lookup = new FakeLookup
        {
            ["IntelliJ Platform DB — 56ef87d9-f24e-4f97-9d95-6d4babecdad6"] = "hunter2"
        };

        var enriched = ExternalConnectionImport.WithStoredPasswords(found, lookup, PostgresFields);

        var withPassword = enriched.Single(c => c.Name == "orders@prod");
        Assert.Equal("hunter2", withPassword.Values["password"]);
        Assert.True(withPassword.HasPassword);

        // The one the store has nothing for comes through untouched and still importable — a miss is an
        // ordinary answer, not a failure.
        var without = enriched.Single(c => c.Name == "no password here");
        Assert.False(without.HasPassword);
        Assert.True(without.CanImport);
        Assert.False(without.Values.ContainsKey("password"));
    }

    [Fact] // A locked keychain, a declined prompt, a backend that isn't installed: the import still works.
    public void A_lookup_that_throws_leaves_every_row_importable()
    {
        var found = ExternalConnectionImport.FromDataGrip(DataGripXml, PostgresFields);

        var enriched = ExternalConnectionImport.WithStoredPasswords(found, new ThrowingLookup(), PostgresFields);

        Assert.All(enriched, c => Assert.True(c.CanImport));
        Assert.All(enriched, c => Assert.False(c.HasPassword));
    }

    [Fact] // Nothing is asked of the OS for rows that already have a password, or that can't be imported.
    public void Rows_with_nothing_to_fetch_are_never_looked_up()
    {
        var found = ExternalConnectionImport.FromDataGrip(DataGripXml, _ => null);   // provider not installed
        var lookup = new FakeLookup();

        ExternalConnectionImport.WithStoredPasswords(found, lookup, _ => null);

        Assert.Empty(lookup.Asked);
    }

    private static IReadOnlyList<string>? PostgresFields(string providerId) =>
        providerId == "postgres" ? ["host", "port", "database", "username", "password"] : null;

    private sealed class FakeLookup : IForeignSecretLookup
    {
        private readonly Dictionary<string, string> _secrets = [];
        public List<string> Asked { get; } = [];
        public string this[string service] { set => _secrets[service] = value; }

        public string? Find(string service)
        {
            Asked.Add(service);
            return _secrets.GetValueOrDefault(service);
        }
    }

    private sealed class ThrowingLookup : IForeignSecretLookup
    {
        public string? Find(string service) => throw new InvalidOperationException("no secret service running");
    }
}
