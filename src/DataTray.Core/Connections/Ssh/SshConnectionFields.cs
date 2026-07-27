using DataTray.Sdk;

namespace DataTray.Core.Connections.Ssh;

/// <summary>
/// The SSH-tunnel fields the host adds to every connection form, on top of the provider's own
/// <see cref="ConnectionField"/>s. They are host-owned on purpose: tunnelling is a property of how the host
/// reaches the server, not of the database engine, so no provider has to know about it and no plugin has to
/// be rebuilt to gain it. They travel in the same value dictionary as the provider's fields — which is why
/// the keys are prefixed, and why <see cref="ConnectionService"/> strips them again before a provider ever
/// sees the values.
/// </summary>
public static class SshConnectionFields
{
    /// <summary>Prefix that marks a value as host-owned SSH configuration rather than a provider field.</summary>
    public const string Prefix = "ssh.";

    public const string EnabledKey = "ssh.enabled";
    public const string HostKey = "ssh.host";
    public const string PortKey = "ssh.port";
    public const string UsernameKey = "ssh.username";
    public const string AuthKey = "ssh.auth";
    public const string PasswordKey = "ssh.password";
    public const string PrivateKeyKey = "ssh.privateKey";
    public const string PassphraseKey = "ssh.passphrase";
    public const string FingerprintKey = "ssh.hostFingerprint";

    /// <summary><see cref="AuthKey"/> value: authenticate with a password.</summary>
    public const string AuthPassword = "Password";

    /// <summary><see cref="AuthKey"/> value: authenticate with a private key file.</summary>
    public const string AuthPrivateKey = "Private key";

    /// <summary>The value keys a provider may use for the server it connects to. A tunnel has to find the
    /// target host without knowing the provider; this is the same convention the MCP host's allow-list uses.
    /// </summary>
    public static IReadOnlyList<string> TargetHostKeys { get; } = ["host", "server", "hostname", "endpoint"];

    /// <summary>The value key for the target port. Every provider that has a host has this too.</summary>
    public const string TargetPortKey = "port";

    private const string Section = "SSH tunnel";

    /// <summary>The fields, in form order. All advanced: a tunnel is the exception, not the common case.</summary>
    public static IReadOnlyList<ConnectionField> All { get; } =
    [
        new(EnabledKey, "Connect through an SSH tunnel", ConnectionFieldType.Bool,
            Default: "false", Group: Section, Advanced: true),
        new(HostKey, "SSH host", ConnectionFieldType.Text,
            Placeholder: "bastion.example.com", Group: Section, Advanced: true),
        new(PortKey, "SSH port", ConnectionFieldType.Number,
            Default: "22", Group: Section, Advanced: true),
        new(UsernameKey, "SSH user", ConnectionFieldType.Text, Group: Section, Advanced: true),
        new(AuthKey, "Authenticate with", ConnectionFieldType.Choice,
            Default: AuthPassword, Group: Section, Advanced: true,
            Choices: [AuthPassword, AuthPrivateKey]),
        new(PasswordKey, "SSH password", ConnectionFieldType.Password, Group: Section, Advanced: true),
        new(PrivateKeyKey, "Private key file", ConnectionFieldType.File, Group: Section, Advanced: true),
        new(PassphraseKey, "Key passphrase", ConnectionFieldType.Password, Group: Section, Advanced: true),
        new(FingerprintKey, "Expected host fingerprint", ConnectionFieldType.Text,
            Placeholder: "SHA256:… (optional; pins the server's host key)", Group: Section, Advanced: true)
    ];

    /// <summary>True when the key belongs to the host's SSH block rather than to a provider field.</summary>
    public static bool IsSshKey(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Whether a tunnel means anything for this provider: it does once the provider connects to a
    /// host. A file-backed engine such as SQLite has nothing to forward, so it is not offered the section.
    /// </summary>
    public static bool AppliesTo(IEnumerable<ConnectionField> providerFields) =>
        providerFields.Any(f => TargetHostKeys.Contains(f.Key));
}
