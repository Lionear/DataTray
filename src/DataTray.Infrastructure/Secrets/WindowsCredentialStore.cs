using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DataTray.Core.Connections;

namespace DataTray.Infrastructure.Secrets;

/// <summary>
/// Windows backend over Credential Manager (advapi32 Cred* APIs) — the Windows equivalent of the
/// macOS Keychain / Linux Secret Service. NOT yet runtime-verified; test on Windows before shipping.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ISecretStore
{
    private const string TargetPrefix = "SqlExplorer:";
    // Pre-rebrand prefix. Credentials written by older builds are migrated to TargetPrefix on first use.
    private const string LegacyTargetPrefix = "Lionear.SqlExplorer:";
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    public WindowsCredentialStore() => MigrateLegacyCredentials();

    public void Set(string key, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = TargetPrefix + key,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException($"CredWrite failed (Win32 {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public string? Get(string key)
    {
        if (!CredRead(TargetPrefix + key, CRED_TYPE_GENERIC, 0, out var handle))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
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

    public void Delete(string key) => CredDelete(TargetPrefix + key, CRED_TYPE_GENERIC, 0);

    /// <summary>
    /// One-time, idempotent migration of pre-rebrand credentials: any entry stored under the legacy
    /// "Lionear.SqlExplorer:" prefix is rewritten under the current "SqlExplorer:" prefix and the old
    /// entry removed. A no-op once migrated (the enumerate filter then matches nothing).
    /// </summary>
    private void MigrateLegacyCredentials()
    {
        if (!CredEnumerate(LegacyTargetPrefix + "*", 0, out var count, out var credsPtr) || credsPtr == IntPtr.Zero)
        {
            return;
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                var entryPtr = Marshal.ReadIntPtr(credsPtr, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIAL>(entryPtr);
                if (cred.TargetName is null || !cred.TargetName.StartsWith(LegacyTargetPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var key = cred.TargetName[LegacyTargetPrefix.Length..];
                var secret = cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0
                    ? Encoding.Unicode.GetString(ReadBlob(cred.CredentialBlob, (int)cred.CredentialBlobSize))
                    : string.Empty;

                // Set() is an instance method that does not re-enter the constructor, so no recursion.
                Set(key, secret);
                CredDelete(cred.TargetName, CRED_TYPE_GENERIC, 0);
            }
        }
        finally
        {
            CredFree(credsPtr);
        }
    }

    private static byte[] ReadBlob(IntPtr ptr, int size)
    {
        var bytes = new byte[size];
        Marshal.Copy(ptr, bytes, 0, size);
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredEnumerateW")]
    private static extern bool CredEnumerate(string? filter, uint flags, out int count, out IntPtr credentialsPtr);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
