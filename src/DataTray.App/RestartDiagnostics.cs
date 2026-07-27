using System;
using System.IO;
using DataTray.Core;

namespace DataTray.App;

/// <summary>
/// Best-effort diagnostic log for the relaunch flow (SE-125): the "Restart app" button and the in-app
/// updater's relaunch have been intermittently failing to bring a new instance back (mainly on Fedora), so
/// both the outgoing process (spawn target + result) and the incoming one (which start path it took) append
/// here. One file, PID-stamped, so the full parent→child sequence is visible after a failed restart.
/// Never throws — diagnostics must not be able to break a restart.
/// </summary>
public static class RestartDiagnostics
{
    private static string LogPath => AppPaths.File("restart.log");

    public static void Log(string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [pid {Environment.ProcessId}] {message}{Environment.NewLine}";
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, line);
            System.Diagnostics.Trace.WriteLine("[restart] " + message);
        }
        catch
        {
            // Diagnostics are best-effort — a logging failure must never affect the restart itself.
        }
    }
}
