namespace SqlExplorer.Core.Connections.Ssh;

/// <summary>The loopback endpoint a tunnel forwards from; what the provider connects to instead of the
/// real server.</summary>
public readonly record struct SshTunnelEndpoint(string Host, int Port);

/// <summary>
/// Owns the live SSH tunnels. Tunnels are keyed by <see cref="SshTunnelSettings.TunnelKey"/>, not by
/// connection: nothing in the app holds an open database connection (every provider call opens and disposes
/// its own), so a tunnel cannot be scoped to one. It is opened on first use and shared by every connection
/// that asks for the same route.
/// </summary>
public interface ISshTunnelManager
{
    /// <summary>Open the tunnel, or hand back the one already forwarding this route. Blocks for the SSH
    /// handshake on first use.</summary>
    SshTunnelEndpoint Open(SshTunnelSettings settings, string targetHost, int targetPort);

    /// <summary>Tear down the tunnel for this route, if one is up. Safe to call for a route that never had
    /// one.</summary>
    void Close(SshTunnelSettings settings, string targetHost, int targetPort);

    /// <summary>Tear every tunnel down — used on shutdown.</summary>
    void CloseAll();
}
