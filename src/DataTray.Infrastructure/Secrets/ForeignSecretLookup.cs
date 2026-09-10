using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DataTray.Core.Connections.Import;

namespace DataTray.Infrastructure.Secrets;

/// <summary>Picks the read-only foreign-secret backend for this OS (SE-238).</summary>
public static class ForeignSecretLookups
{
    /// <summary>The backend for the running platform, or a lookup that finds nothing where there is no
    /// credential store to ask — an unsupported OS is "no password available", not a failure.</summary>
    public static IForeignSecretLookup ForThisPlatform()
    {
        if (OperatingSystem.IsLinux())
        {
            return new LinuxForeignSecretLookup();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacForeignSecretLookup();
        }

        return OperatingSystem.IsWindows()
            ? new WindowsForeignSecretLookup()
            : new NoForeignSecrets();
    }
}

/// <summary>Finds nothing, always. Used where the platform has no credential store to ask.</summary>
public sealed class NoForeignSecrets : IForeignSecretLookup
{
    public string? Find(string service) => null;
}

/// <summary>
/// Linux: the freedesktop Secret Service (gnome-keyring / kwallet) through libsecret's
/// <c>secret-tool</c>, the same tool <see cref="LinuxSecretServiceStore"/> uses for our own secrets.
/// </summary>
/// <remarks>
/// Verified on Fedora 44 + KWallet against a real DataGrip profile. A locked wallet makes secret-tool
/// prompt; that prompt is the point and is never suppressed. Declining exits non-zero, which reads here as
/// "no password", exactly like a missing entry.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxForeignSecretLookup : IForeignSecretLookup
{
    public string? Find(string service)
    {
        var psi = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("lookup");
        psi.ArgumentList.Add("service");
        psi.ArgumentList.Add(service);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            // No libsecret installed is a perfectly normal machine, not an error worth surfacing.
            return null;
        }
    }
}

/// <summary>
/// macOS: a generic-password item in the login Keychain, read through the <c>security</c> CLI.
/// </summary>
/// <remarks>
/// The CLI rather than Security.framework on purpose: <c>security</c> asks the user per item for an entry
/// another application owns, which is the consent step this feature is built around. NOT runtime-verified —
/// test on macOS before shipping, like <see cref="MacKeychainStore"/>.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacForeignSecretLookup : IForeignSecretLookup
{
    public string? Find(string service)
    {
        var psi = new ProcessStartInfo("/usr/bin/security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("find-generic-password");
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(service);
        psi.ArgumentList.Add("-w");

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            // -w prints the password alone, with a trailing newline.
            return process.ExitCode == 0 ? output.TrimEnd('\n') : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Windows: Credential Manager, read by target name. NOT runtime-verified — test on Windows before
/// shipping, like <see cref="WindowsCredentialStore"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsForeignSecretLookup : IForeignSecretLookup
{
    private const uint CredTypeGeneric = 1;

    public string? Find(string service)
    {
        if (!CredRead(service, CredTypeGeneric, 0, out var handle))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<ForeignCredential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(handle);
        }
    }

    // Only the fields this read needs; the rest of CREDENTIAL is laid out after them and left unread.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ForeignCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
