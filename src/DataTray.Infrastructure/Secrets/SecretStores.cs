using System.Runtime.InteropServices;
using DataTray.Core.Connections;
using DataTray.Core.Security;

namespace DataTray.Infrastructure.Secrets;

/// <summary>Picks the OS-native credential vault. Never returns a plaintext fallback.</summary>
public static class SecretStores
{
    /// <summary>
    /// The store the app runs on, per the user's onboarding choice (SE-292): the OS vault with the optional
    /// master-password layer over it, or the encrypted file store, which has no plaintext mode at all.
    /// </summary>
    public static ISecretStore Create(bool useFileStore, IMasterKeyProvider keys)
    {
        if (useFileStore)
        {
            return CreateFile(keys);
        }

        // No vault on this machine and no file store chosen: rather than crash the app at startup (which is
        // what a bare CreateForCurrentOs does on a Linux box with no Secret Service), stand up the file store
        // so onboarding can still run. Its Set throws until a master password is set, which is precisely the
        // one thing the Security step exists to do.
        return TryCreateForCurrentOs() is { } vault
            ? new EncryptingSecretStore(vault, keys)
            : CreateFile(keys);
    }

    /// <summary>The encrypted file store: AES-GCM under the master password, no plaintext path.</summary>
    public static ISecretStore CreateFile(IMasterKeyProvider keys, string? path = null) =>
        new EncryptingSecretStore(new FileSecretStore(path), keys, requireKey: true);

    /// <summary>Whether this machine has a usable OS vault — false disables the vault card in onboarding
    /// instead of letting the user pick a store that cannot store anything.</summary>
    public static bool IsOsVaultAvailable() => TryCreateForCurrentOs() is not null;

    /// <summary><see cref="CreateForCurrentOs"/>, or null on a machine with no vault to talk to.</summary>
    public static ISecretStore? TryCreateForCurrentOs()
    {
        // The Linux backend shells out to secret-tool, so no libsecret means no vault. Probing PATH here is
        // worth the few lines: the alternative is finding out at the first Set, long after the user chose.
        if (OperatingSystem.IsLinux() && !OnPath("secret-tool"))
        {
            return null;
        }

        try
        {
            return CreateForCurrentOs();
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static bool OnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(dir => File.Exists(Path.Combine(dir, executable)));

    /// <summary>
    /// The platform store, wrapped so credentials written before the DataTray rename are still found and
    /// pulled forward when read (SE-206). See <see cref="LegacyFallbackSecretStore"/> for why that is
    /// lazy rather than a sweep at startup.
    /// </summary>
    public static ISecretStore CreateForCurrentOs()
    {
        if (OperatingSystem.IsWindows())
        {
            return new LegacyFallbackSecretStore(
                new WindowsCredentialStore(),
                new WindowsCredentialStore(WindowsCredentialStore.LegacyPrefix));
        }

        if (OperatingSystem.IsMacOS())
        {
            return new LegacyFallbackSecretStore(
                new MacKeychainStore(),
                new MacKeychainStore(MacKeychainStore.LegacyService));
        }

        if (OperatingSystem.IsLinux())
        {
            return new LegacyFallbackSecretStore(
                new LinuxSecretServiceStore(),
                new LinuxSecretServiceStore(LinuxSecretServiceStore.LegacyService));
        }

        throw new PlatformNotSupportedException(
            $"No secure credential store implemented for this OS ({RuntimeInformation.OSDescription}).");
    }
}
