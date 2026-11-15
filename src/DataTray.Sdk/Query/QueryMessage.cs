namespace DataTray.Sdk.Query;

/// <summary>
/// One message the server sent while a script ran (host API v31, SE-300). Providers hand these to the
/// <see cref="IProgress{T}"/> passed to
/// <see cref="IDbProvider.ExecuteScriptAsync(Connections.ConnectionProfile, string, IProgress{QueryMessage}, System.Threading.CancellationToken)"/>
/// as soon as the driver raises them, from whatever thread the driver raises them on; the host stamps the time
/// of arrival and does the marshalling.
/// </summary>
/// <param name="Kind">How the host marks and filters the message.</param>
/// <param name="Text">The message text as the server sent it.</param>
public sealed record QueryMessage(QueryMessageKind Kind, string Text)
{
    /// <summary>A server header shown above the text of an error, e.g.
    /// <c>Msg 547, Level 16, State 0, Line 7</c>. Null when the engine has none.</summary>
    public string? Header { get; init; }

    /// <summary>A short code naming an error in the status bar (<c>Msg 547</c>, <c>22012</c>). Null when the
    /// engine has none.</summary>
    public string? Code { get; init; }
}
