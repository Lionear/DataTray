namespace DataTray.Core.Update;

/// <summary>
/// Whether this install can replace itself. Not an error condition: running from a build directory,
/// an unpacked archive or <c>dotnet run</c> is an ordinary way to run DataTray, and the UI then points
/// at the download page instead of offering a button that cannot do anything.
/// </summary>
public enum UpdateSupport
{
    /// <summary>Managed by the updater — it can download a new build and apply it in place.</summary>
    Supported,

    /// <summary>Not a managed install, so there is nothing here to update.</summary>
    NotPackaged
}
