using DataTray.Core.Connections;
using DataTray.Core.Connections.Ssh;
using DataTray.Core.Providers;
using DataTray.Core.Tests.Mcp;
using DataTray.Sdk;
using DataTray.Sdk.Branding;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Ddl;
using DataTray.Sdk.Query;
using DataTray.Sdk.Schema;

namespace DataTray.Core.Tests.Connections;

// SE-18. The tunnel itself needs an SSH server, so what is covered here is everything around it: reading the
// ssh.* block, keeping its secrets out of the config file, and — the part providers depend on — rewriting
// host/port to the local end of the tunnel before a connection string is ever built.
public class SshTunnelTests
{
    private static Dictionary<string, string?> Tunnelled(params (string Key, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["host"] = "db.internal",
            ["port"] = "5432",
            ["password"] = "dbpw",
            [SshConnectionFields.EnabledKey] = "true",
            [SshConnectionFields.HostKey] = "bastion.example.com",
            [SshConnectionFields.PortKey] = "22",
            [SshConnectionFields.UsernameKey] = "rick",
            [SshConnectionFields.PasswordKey] = "sshpw"
        };

        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return values;
    }

    private static (ConnectionService Service, EchoProvider Provider, SpyTunnels Tunnels, RecordingSecretStore Secrets) NewService()
    {
        var provider = new EchoProvider();
        var providers = new DbProviderRegistry([new ProviderRegistration("fake", provider)]);
        var secrets = new RecordingSecretStore();
        var tunnels = new SpyTunnels();
        return (new ConnectionService(new FakeConnectionStore(), secrets, providers, tunnels), provider, tunnels, secrets);
    }

    [Fact]
    public void From_returns_null_when_the_tunnel_is_off()
    {
        Assert.Null(SshTunnelSettings.From(new Dictionary<string, string?> { ["host"] = "db.internal" }));
        Assert.Null(SshTunnelSettings.From(Tunnelled((SshConnectionFields.EnabledKey, "false"))));
    }

    [Fact]
    public void From_reads_the_ssh_block()
    {
        var settings = SshTunnelSettings.From(Tunnelled())!;

        Assert.Equal("bastion.example.com", settings.Host);
        Assert.Equal(22, settings.Port);
        Assert.Equal("rick", settings.Username);
        Assert.Equal(SshAuthMethod.Password, settings.Auth);
        Assert.Equal("sshpw", settings.Password);
    }

    [Fact] // An empty required field is a mistake worth naming, rather than an opaque SSH error later.
    public void From_names_the_field_that_is_missing()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SshTunnelSettings.From(Tunnelled((SshConnectionFields.UsernameKey, ""))));

        Assert.Contains("SSH user", error.Message);
    }

    [Fact]
    public void From_rejects_key_authentication_without_a_key_file()
    {
        Assert.Throws<InvalidOperationException>(
            () => SshTunnelSettings.From(Tunnelled((SshConnectionFields.AuthKey, SshConnectionFields.AuthPrivateKey))));
    }

    [Fact] // Two connections to the same server over the same bastion share one forward; a different one does not.
    public void TunnelKey_identifies_the_route()
    {
        var settings = SshTunnelSettings.From(Tunnelled())!;

        Assert.Equal(settings.TunnelKey("db.internal", 5432), settings.TunnelKey("db.internal", 5432));
        Assert.NotEqual(settings.TunnelKey("db.internal", 5432), settings.TunnelKey("other.internal", 5432));
        Assert.NotEqual(settings.TunnelKey("db.internal", 5432), settings.TunnelKey("db.internal", 5433));
    }

    [Fact] // The whole point: the provider builds its connection string against the local end of the tunnel.
    public void BuildProfile_points_the_provider_at_the_forwarded_port()
    {
        var (service, provider, tunnels, _) = NewService();

        service.BuildProfile("Conn", "fake", Tunnelled());

        Assert.Equal("127.0.0.1", provider.LastValues["host"]);
        Assert.Equal(tunnels.Endpoint.Port.ToString(), provider.LastValues["port"]);
        Assert.Equal(("db.internal", 5432), (tunnels.LastTargetHost, tunnels.LastTargetPort));
    }

    [Fact] // ssh.* is host bookkeeping; no provider should ever have to know the key exists.
    public void BuildProfile_hides_the_ssh_keys_from_the_provider()
    {
        var (service, provider, _, _) = NewService();

        service.BuildProfile("Conn", "fake", Tunnelled());

        Assert.DoesNotContain(provider.LastValues.Keys, SshConnectionFields.IsSshKey);
        Assert.Equal("dbpw", provider.LastValues["password"]);  // the database password still gets through
    }

    [Fact]
    public void BuildProfile_leaves_an_untunnelled_connection_alone()
    {
        var (service, provider, tunnels, _) = NewService();

        service.BuildProfile("Conn", "fake", new Dictionary<string, string?> { ["host"] = "db.internal", ["port"] = "5432" });

        Assert.Equal("db.internal", provider.LastValues["host"]);
        Assert.Equal(0, tunnels.Opened);
    }

    [Fact] // A blank port field still has to forward somewhere: the provider's declared default.
    public void BuildProfile_falls_back_to_the_providers_default_port()
    {
        var (service, _, tunnels, _) = NewService();

        service.BuildProfile("Conn", "fake", Tunnelled(("port", "")));

        Assert.Equal(5432, tunnels.LastTargetPort);
    }

    [Fact]
    public void Resolve_tunnels_a_saved_connection_too()
    {
        var (service, provider, tunnels, _) = NewService();
        var saved = service.Save("c1", "Conn", "fake", Tunnelled());

        service.Resolve(saved);

        Assert.Equal(1, tunnels.Opened);
        Assert.Equal("127.0.0.1", provider.LastValues["host"]);
    }

    [Fact] // The SSH password is a secret like any other: keychain, never connections.json.
    public void Save_keeps_the_ssh_secrets_out_of_the_config_file()
    {
        var (service, _, _, secrets) = NewService();

        var saved = service.Save("c1", "Conn", "fake", Tunnelled((SshConnectionFields.PassphraseKey, "keypw")));

        Assert.Equal("sshpw", secrets.Secrets[$"conn:c1:{SshConnectionFields.PasswordKey}"]);
        Assert.Equal("keypw", secrets.Secrets[$"conn:c1:{SshConnectionFields.PassphraseKey}"]);
        Assert.DoesNotContain(SshConnectionFields.PasswordKey, saved.Values.Keys);
        Assert.DoesNotContain(SshConnectionFields.PassphraseKey, saved.Values.Keys);
        Assert.Equal("bastion.example.com", saved.Values[SshConnectionFields.HostKey]);  // the route is not a secret
    }

    [Fact]
    public void Delete_removes_the_ssh_secrets_as_well()
    {
        var (service, _, _, secrets) = NewService();
        service.Save("c1", "Conn", "fake", Tunnelled());

        service.Delete("c1");

        Assert.DoesNotContain(secrets.Secrets.Keys, k => k.StartsWith("conn:c1:", StringComparison.Ordinal));
    }

    [Fact]
    public void CloseTunnel_closes_the_route_the_connection_uses()
    {
        var (service, _, tunnels, _) = NewService();
        var saved = service.Save("c1", "Conn", "fake", Tunnelled());
        service.Resolve(saved);

        service.CloseTunnel(saved);

        Assert.Equal(1, tunnels.Closed);
    }

    [Fact] // Without a tunnel service a tunnelled connection must fail loudly, not connect straight to the server.
    public void A_tunnelled_connection_is_refused_when_no_tunnel_service_is_available()
    {
        var provider = new EchoProvider();
        var service = new ConnectionService(
            new FakeConnectionStore(), new RecordingSecretStore(),
            new DbProviderRegistry([new ProviderRegistration("fake", provider)]));

        Assert.Throws<InvalidOperationException>(() => service.BuildProfile("Conn", "fake", Tunnelled()));
    }

    // Records the values it was asked to build from, so a test can assert on what the host handed over.
    private sealed class EchoProvider : IDbProvider
    {
        public IReadOnlyDictionary<string, string?> LastValues { get; private set; } =
            new Dictionary<string, string?>();

        public string DisplayName => "Echo";
        public ProviderIcon? Icon => null;
        public ISqlDialect Dialect => throw new NotSupportedException();
        public bool IsSqlBased => true;

        public IReadOnlyList<ConnectionField> ConnectionFields =>
        [
            new("host", "Host", ConnectionFieldType.Text, Required: true),
            new("port", "Port", ConnectionFieldType.Number, Default: "5432"),
            new("password", "Password", ConnectionFieldType.Password)
        ];

        public string BuildConnectionString(IReadOnlyDictionary<string, string?> values)
        {
            LastValues = new Dictionary<string, string?>(values);
            return string.Join(';', values.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        public Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ExecuteBatchAsync(ConnectionProfile profile, IReadOnlyList<SqlStatement> statements, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<CreateCapability> CreateCapabilities => [];
        public IReadOnlyList<string> ColumnTypes => [];
        public SqlStatement BuildCreateStatement(CreateObjectSpec spec) => throw new NotSupportedException();
        public Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
    }

    // Stands in for the SSH.NET-backed manager: no server, but the same contract.
    private sealed class SpyTunnels : ISshTunnelManager
    {
        public SshTunnelEndpoint Endpoint { get; } = new("127.0.0.1", 49152);
        public int Opened { get; private set; }
        public int Closed { get; private set; }
        public string? LastTargetHost { get; private set; }
        public int LastTargetPort { get; private set; }

        public SshTunnelEndpoint Open(SshTunnelSettings settings, string targetHost, int targetPort)
        {
            Opened++;
            LastTargetHost = targetHost;
            LastTargetPort = targetPort;
            return Endpoint;
        }

        public void Close(SshTunnelSettings settings, string targetHost, int targetPort) => Closed++;

        public void CloseAll() => Closed++;
    }
}
