using System.Diagnostics;
using Microsoft.Win32;

namespace DataTray.Infrastructure.Update;

/// <summary>
/// The Inno Setup install left behind when a Windows user crosses over to Velopack (SE-245).
///
/// <para>The two installers do not share a location: Inno put DataTray under
/// <c>%LOCALAPPDATA%\Programs</c>, Velopack installs to <c>%LOCALAPPDATA%\DataTray</c>. So the old
/// install is not upgraded, it is simply still there — with its own Start-menu entry and uninstaller.
/// That is not merely untidy: the old shortcut still launches the old build against the <em>same</em>
/// app-data folder, which is exactly the hazard the SQL Explorer rename ran into. Someone starting it
/// from a pinned taskbar button is silently back on the previous version.</para>
///
/// <para>Velopack documents no migration path from another installer (only from Squirrel and
/// ClickOnce), so this is our own: find the entry Inno registered, and offer to run its uninstaller.
/// Offer — never do it unprompted. Velopack has already installed a second copy, so removing the first
/// one takes away something the user still has, and that asks first.</para>
/// </summary>
public static class LegacyWindowsInstall
{
    // The AppId the Inno script carried, unchanged since before the SQL Explorer -> DataTray rename
    // precisely so upgrades would find their predecessor. "_is1" is Inno's own suffix.
    private const string UninstallSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3A6C21-4E7B-4D19-9A2E-6C5B1D0E7F84}_is1";

    /// <summary>
    /// The old install's uninstaller, or null when there is none (and always null off Windows).
    /// <para>
    /// Both hives are searched. The installer was per-user by default, which lands in HKCU, but its
    /// <c>PrivilegesRequiredOverridesAllowed=dialog</c> let anyone elevate to a machine-wide install
    /// instead — and that one registers in HKLM. Checking only HKCU would quietly miss exactly the
    /// users who clicked through an elevation prompt.
    /// </para>
    /// </summary>
    public static string? FindUninstaller()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(UninstallSubKey);
                var path = ParseUninstallerPath(key?.GetValue("UninstallString") as string);
                if (path is not null && File.Exists(path))
                {
                    return path;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // A hive we may not read is a hive with no answer, not a failure worth surfacing.
            }
        }

        return null;
    }

    /// <summary>
    /// Pull the executable out of an <c>UninstallString</c>. Inno writes it quoted when the path holds
    /// spaces — which it does for every default install, because the path runs through the user's
    /// profile — and bare when it does not. It may also carry trailing switches, so only the leading
    /// program is taken.
    /// </summary>
    public static string? ParseUninstallerPath(string? uninstallString)
    {
        var value = uninstallString?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value[0] == '"')
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }

        // Unquoted: everything up to the first switch. A bare path with spaces and no switches stays
        // whole, which is the only reading that can be right without guessing where the path ends.
        var cut = value.IndexOf(" /", StringComparison.Ordinal);
        var path = (cut > 0 ? value[..cut] : value).Trim();
        return path.Length > 0 ? path : null;
    }

    /// <summary>
    /// Run the old uninstaller without a wizard. Inno's own uninstaller accepts these switches — this
    /// is the opposite direction from vpk's Setup.exe, which ignores them.
    /// </summary>
    public static void Remove(string uninstallerPath) =>
        Process.Start(new ProcessStartInfo(uninstallerPath)
        {
            UseShellExecute = false,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
        });
}
