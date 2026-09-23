using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace DataTray.App.Views;

/// <summary>
/// Puts macOS's traffic lights where a taller title bar needs them (SE-291).
/// <para>
/// Avalonia does not place them: its macOS backend only hides or shows the three standard buttons
/// (<c>WindowImpl.mm</c>, <c>UpdateAppearance</c>), and <c>ExtendClientAreaTitleBarHeightHint</c> merely
/// sizes Avalonia's own title-bar material (<c>AutoFitContentView.mm</c>). AppKit lays the buttons out
/// itself, centred in the system title bar — 28 pt. Draw a bar taller than that and they sit high in it,
/// which is the misalignment this fixes.
/// </para>
/// <para>
/// The route is the one the apps with tall bars take: give the window an empty toolbar in the unified
/// compact style. AppKit then treats title bar and toolbar as one region and centres the buttons in it,
/// so their layout — and their hit testing — stays AppKit's business. Moving the buttons by hand is the
/// other option and a worse one: they would leave their superview's bounds, which is where clicks stop
/// landing on them.
/// </para>
/// <para>
/// Never throws. If any of this fails, or the OS is too old, the window keeps its buttons where macOS
/// put them — a bar that looks slightly off, not an app that will not start.
/// </para>
/// </summary>
internal static class MacTitleBar
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>NSWindowToolbarStyleUnifiedCompact.</summary>
    private const long UnifiedCompact = 4;

    public static void UseUnifiedTitleBar(Window window)
    {
        // setToolbarStyle: is macOS 11+. Below that the unified style does not exist and an empty toolbar
        // would only add a visible strip, so leave the window alone.
        if (!OperatingSystem.IsMacOSVersionAtLeast(11))
        {
            return;
        }

        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "NSWindow" } handle
            || handle.Handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var toolbarClass = objc_getClass("NSToolbar");
            if (toolbarClass == IntPtr.Zero)
            {
                return;
            }

            var toolbar = SendPointer(SendPointer(toolbarClass, Selector("alloc")), Selector("init"));
            if (toolbar == IntPtr.Zero)
            {
                return;
            }

            // An empty toolbar is there for its height, not to be seen: no baseline, nothing in it.
            SendVoidBool(toolbar, Selector("setShowsBaselineSeparator:"), false);
            SendVoidPointer(handle.Handle, Selector("setToolbar:"), toolbar);
            SendVoidLong(handle.Handle, Selector("setToolbarStyle:"), UnifiedCompact);
        }
        catch (Exception)
        {
            // A title bar is not worth a failed launch.
        }
    }

    private static IntPtr Selector(string name) => sel_registerName(name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    // Only pointer- and integer-sized arguments and returns are used here, deliberately: a struct return
    // (an NSRect from -frame, say) goes through objc_msgSend_stret on x86_64 and plain objc_msgSend on
    // arm64, and getting that wrong is a crash on one of the two architectures.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidPointer(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidLong(IntPtr receiver, IntPtr selector, long argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool argument);
}
