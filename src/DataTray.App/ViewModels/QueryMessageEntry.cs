using DataTray.Sdk.Query;

namespace DataTray.App.ViewModels;

/// <summary>One line of a query tab's Messages log (SE-300): a server message stamped with its arrival time,
/// a client line the app wrote itself (the cancel note), or the divider between kept runs.</summary>
public sealed record QueryMessageEntry(QueryMessageKind Kind, string Text, DateTimeOffset Time)
{
    public string? Header { get; init; }

    public string? Code { get; init; }

    /// <summary>Written by the app, not the server — shown in italics.</summary>
    public bool IsClient { get; init; }

    /// <summary>The "Run 2 · 14:09:31" divider when messages are kept between runs.</summary>
    public bool IsSeparator { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss.fff");

    public bool HasHeader => Header is not null;
}
