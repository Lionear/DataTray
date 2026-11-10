namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>What the pre-scan found: script findings, the comment count, and where the (first) statement starts.</summary>
internal sealed record ScriptScan(IReadOnlyList<ReadReason> Reasons, int CommentCount, int StatementCount, int FirstLine);
