using System.Globalization;

namespace DataTray.Core.Connections.Ssh;

/// <summary>How the tunnel authenticates against the SSH server.</summary>
public enum SshAuthMethod
{
    Password,
    PrivateKey
}

/// <summary>
/// The SSH half of a connection's values, parsed once into something typed. Built by
/// <see cref="From"/> straight from the value dictionary a connection carries, so it works the same for a
/// saved connection, a transient one and the unsaved values behind the dialog's Test button.
/// </summary>
public sealed record SshTunnelSettings
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public required string Username { get; init; }

    public required SshAuthMethod Auth { get; init; }

    public string? Password { get; init; }

    public string? PrivateKeyPath { get; init; }

    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>Optional SHA256 fingerprint of the SSH server's host key. When set, a server presenting a
    /// different key is refused instead of trusted.</summary>
    public string? HostKeyFingerprint { get; init; }

    /// <summary>
    /// Reads the <c>ssh.*</c> values, or null when the connection does not tunnel. Throws when the tunnel is
    /// switched on but under-specified — better a named missing field than an opaque SSH error later.
    /// </summary>
    public static SshTunnelSettings? From(IReadOnlyDictionary<string, string?> values)
    {
        if (!IsEnabled(values))
        {
            return null;
        }

        var auth = Value(values, SshConnectionFields.AuthKey) == SshConnectionFields.AuthPrivateKey
            ? SshAuthMethod.PrivateKey
            : SshAuthMethod.Password;

        var settings = new SshTunnelSettings
        {
            Host = Require(values, SshConnectionFields.HostKey, "SSH host"),
            Port = ParsePort(Value(values, SshConnectionFields.PortKey)),
            Username = Require(values, SshConnectionFields.UsernameKey, "SSH user"),
            Auth = auth,
            Password = Value(values, SshConnectionFields.PasswordKey),
            PrivateKeyPath = Value(values, SshConnectionFields.PrivateKeyKey),
            PrivateKeyPassphrase = Value(values, SshConnectionFields.PassphraseKey),
            HostKeyFingerprint = Value(values, SshConnectionFields.FingerprintKey)
        };

        if (settings.Auth == SshAuthMethod.PrivateKey && string.IsNullOrWhiteSpace(settings.PrivateKeyPath))
        {
            throw new InvalidOperationException("The SSH tunnel is set to key authentication but no private key file is set.");
        }

        return settings;
    }

    /// <summary>Identity of the tunnel this configuration asks for, target included: two connections that
    /// agree on all of it can share one forwarded port instead of opening a second identical tunnel. The
    /// secrets are deliberately not part of it — they authenticate the tunnel, they do not identify it.</summary>
    public string TunnelKey(string targetHost, int targetPort) =>
        string.Join('|', Host, Port, Username, Auth, PrivateKeyPath ?? string.Empty, targetHost, targetPort);

    private static bool IsEnabled(IReadOnlyDictionary<string, string?> values) =>
        bool.TryParse(Value(values, SshConnectionFields.EnabledKey), out var enabled) && enabled;

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string Require(IReadOnlyDictionary<string, string?> values, string key, string label) =>
        Value(values, key) ?? throw new InvalidOperationException($"The SSH tunnel is switched on but the {label} is empty.");

    private static int ParsePort(string? value) =>
        value is null ? 22
        : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port
        : throw new InvalidOperationException($"'{value}' is not a valid SSH port.");
}
