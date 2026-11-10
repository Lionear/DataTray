using DataTray.Extensions.QueryBuilder.Model;

namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>The read statement (null when CannotShow), the reasons it can't be shown, and what reading dropped or kept as text.</summary>
public sealed record ReadResult(ReadOutcome Outcome, Statement? Statement, IReadOnlyList<ReadReason> Reasons, int FragmentCount, int CommentCount);
