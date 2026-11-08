using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DataTray.App.Startup;

/// <summary>
/// Whether the OS asks for reduced motion. Avalonia has no API for it, so this reads the setting per platform:
/// Windows client-area animation, macOS "Reduce motion", GNOME "Animations". Anything unknown means motion on.
/// </summary>
public static class ReducedMotion
{
    public const string OverrideVariable = "DATATRAY_REDUCE_MOTION";

    private const uint SpiGetClientAreaAnimation = 0x1042;
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public static bool IsRequested()
    {
        if (Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } value)
        {
            return value is "1" or "true";
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) && !enabled;
            }

            if (OperatingSystem.IsMacOS())
            {
                return MacReduceMotion();
            }

            if (OperatingSystem.IsLinux())
            {
                return GnomeAnimationsOff();
            }
        }
        catch (Exception)
        {
            // A missing API or tool is not a reason to block the splash: fall back to motion on.
        }

        return false;
    }

    private static bool GnomeAnimationsOff()
    {
        // ponytail: GNOME only (KDE and others keep motion on); half a second at most, then motion on.
        using var process = Process.Start(new ProcessStartInfo("gsettings", "get org.gnome.desktop.interface enable-animations")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });
        if (process is null || !process.WaitForExit(500))
        {
            return false;
        }

        return process.StandardOutput.ReadToEnd().Trim() == "false";
    }

    private static bool MacReduceMotion()
    {
        var workspace = objc_msgSend(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
        return workspace != IntPtr.Zero
            && objc_msgSend_bool(workspace, sel_registerName("accessibilityDisplayShouldReduceMotion"));
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool objc_msgSend_bool(IntPtr receiver, IntPtr selector);
}
