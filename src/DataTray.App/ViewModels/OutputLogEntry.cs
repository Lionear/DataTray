namespace DataTray.App.ViewModels;

/// <summary>Severity of an <see cref="OutputLogEntry"/>; drives the badge colour in the output panel.</summary>
public enum OutputLevel
{
    Info,
    Error
}

/// <summary>One line in the output/log panel: when it happened, how bad it was, which connection it
/// came from, and the message. A failure also pops the panel open (see MainViewModel).</summary>
public sealed record OutputLogEntry(DateTime Time, OutputLevel Level, string? Source, string Message)
{
    public bool IsError => Level == OutputLevel.Error;
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => IsError ? "ERROR" : "OK";

    /// <summary>The whole row as one plain-text line, for the "Copy" context-menu action.</summary>
    public string CopyText => Source is { Length: > 0 }
        ? $"{TimeText}  {LevelText}  {Source}  {Message}"
        : $"{TimeText}  {LevelText}  {Message}";
}
