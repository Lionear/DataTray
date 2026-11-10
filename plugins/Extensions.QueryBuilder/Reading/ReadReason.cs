namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>Why a script can't be shown; Code is stable, Message is for the user, Line is 1-based.</summary>
public sealed record ReadReason(string Code, string Message, int Line);
