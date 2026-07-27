using Renci.SshNet;
using Renci.SshNet.Common;

namespace DataTray.Core.Connections.Ssh;

/// <summary>
/// SSH.NET-backed <see cref="ISshTunnelManager"/>: one <see cref="SshClient"/> per route, each with a single
/// local port forward from loopback to the database server as seen from the bastion.
/// </summary>
public sealed class SshTunnelManager : ISshTunnelManager, IDisposable
{
    // A dropped tunnel is only noticed when its client reports it, so keep the session warm rather than let
    // an idle NAT/firewall silently eat it between two queries.
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, Tunnel> _tunnels = [];
    private readonly Lock _gate = new();

    public SshTunnelEndpoint Open(SshTunnelSettings settings, string targetHost, int targetPort)
    {
        var key = settings.TunnelKey(targetHost, targetPort);

        lock (_gate)
        {
            if (_tunnels.TryGetValue(key, out var existing))
            {
                if (existing.IsAlive)
                {
                    return existing.Endpoint;
                }

                // The bastion dropped us (sleep, network change, server-side idle timeout). Bin the corpse and
                // reconnect rather than hand out a port nothing is listening on any more.
                _tunnels.Remove(key);
                existing.Dispose();
            }

            var tunnel = Connect(settings, targetHost, targetPort);
            _tunnels[key] = tunnel;
            return tunnel.Endpoint;
        }
    }

    public void Close(SshTunnelSettings settings, string targetHost, int targetPort)
    {
        lock (_gate)
        {
            if (_tunnels.Remove(settings.TunnelKey(targetHost, targetPort), out var tunnel))
            {
                tunnel.Dispose();
            }
        }
    }

    public void CloseAll()
    {
        lock (_gate)
        {
            foreach (var tunnel in _tunnels.Values)
            {
                tunnel.Dispose();
            }

            _tunnels.Clear();
        }
    }

    public void Dispose() => CloseAll();

    private static Tunnel Connect(SshTunnelSettings settings, string targetHost, int targetPort)
    {
        var client = new SshClient(ConnectionInfoFor(settings)) { KeepAliveInterval = KeepAlive };

        // The host key is checked during Connect, so a mismatch has to be recorded here and rethrown after —
        // SSH.NET only reports the refusal as a generic connection failure.
        string? presented = null;
        client.HostKeyReceived += (_, e) =>
        {
            presented = e.FingerPrintSHA256;
            e.CanTrust = settings.HostKeyFingerprint is not { } expected || Matches(expected, e.FingerPrintSHA256);
        };

        try
        {
            client.Connect();
        }
        catch (Exception ex) when (settings.HostKeyFingerprint is { } expected && presented is not null && !Matches(expected, presented))
        {
            client.Dispose();
            throw new InvalidOperationException(
                $"The SSH server {settings.Host} presented host key SHA256:{presented}, which does not match the expected {expected}.", ex);
        }
        catch (SshException ex)
        {
            client.Dispose();
            throw new InvalidOperationException($"Could not open the SSH tunnel to {settings.Host}: {ex.Message}", ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        try
        {
            // Port 0: the OS picks a free loopback port, so two tunnels never fight over a fixed one and
            // nothing outside this machine can reach the forward.
            var forward = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
            client.AddForwardedPort(forward);
            forward.Start();
            return new Tunnel(client, forward);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new InvalidOperationException(
                $"The SSH tunnel to {settings.Host} is up but forwarding {targetHost}:{targetPort} failed: {ex.Message}", ex);
        }
    }

    private static ConnectionInfo ConnectionInfoFor(SshTunnelSettings settings)
    {
        AuthenticationMethod method = settings.Auth switch
        {
            SshAuthMethod.PrivateKey => PrivateKeyMethod(settings),
            _ => new PasswordAuthenticationMethod(settings.Username, settings.Password ?? string.Empty)
        };

        return new ConnectionInfo(settings.Host, settings.Port, settings.Username, method);
    }

    private static AuthenticationMethod PrivateKeyMethod(SshTunnelSettings settings)
    {
        var path = settings.PrivateKeyPath!;
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"The SSH private key file '{path}' does not exist.");
        }

        try
        {
            var key = string.IsNullOrEmpty(settings.PrivateKeyPassphrase)
                ? new PrivateKeyFile(path)
                : new PrivateKeyFile(path, settings.PrivateKeyPassphrase);
            return new PrivateKeyAuthenticationMethod(settings.Username, key);
        }
        catch (SshPassPhraseNullOrEmptyException ex)
        {
            throw new InvalidOperationException($"The SSH private key '{path}' is encrypted; fill in the key passphrase.", ex);
        }
        catch (SshException ex)
        {
            throw new InvalidOperationException($"The SSH private key '{path}' could not be read: {ex.Message}", ex);
        }
    }

    // Accepts the fingerprint in the shape ssh-keygen prints it ("SHA256:abc…") as well as bare base64, and
    // ignores the padding OpenSSH leaves off.
    private static bool Matches(string expected, string presented) =>
        string.Equals(
            Normalize(expected),
            Normalize(presented),
            StringComparison.Ordinal);

    private static string Normalize(string fingerprint)
    {
        var value = fingerprint.Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["SHA256:".Length..];
        }

        return value.TrimEnd('=');
    }

    private sealed record Tunnel(SshClient Client, ForwardedPortLocal Forward) : IDisposable
    {
        public SshTunnelEndpoint Endpoint { get; } = new("127.0.0.1", (int)Forward.BoundPort);

        public bool IsAlive => Client.IsConnected && Forward.IsStarted;

        public void Dispose()
        {
            try
            {
                Forward.Stop();
            }
            catch
            {
                // Tearing down a tunnel that is already gone is not a failure worth surfacing.
            }

            Forward.Dispose();
            Client.Dispose();
        }
    }
}
