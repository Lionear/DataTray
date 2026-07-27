using System.Runtime.InteropServices;
using DataTray.Core.Connections;

namespace DataTray.Infrastructure.Secrets;

/// <summary>Picks the OS-native credential vault. Never returns a plaintext fallback.</summary>
public static class SecretStores
{
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
