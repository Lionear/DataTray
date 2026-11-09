namespace DataTray.Extensions.QueryBuilder.Dialects;

public sealed record DialectCapabilities
{
    public bool RightJoin { get; init; }

    public bool FullJoin { get; init; }

    public bool Lateral { get; init; }

    public bool Apply { get; init; }

    public bool RecursiveKeyword { get; init; }

    public bool WindowFunctions { get; init; }

    public bool RangeWithOffset { get; init; }

    public bool GroupsFrame { get; init; }

    public bool FrameExclude { get; init; }

    public bool NamedWindow { get; init; }

    public bool IntersectExcept { get; init; }

    public bool IntersectExceptAll { get; init; }

    public bool Merge { get; init; }

    public bool MergeNotMatchedBySource { get; init; }

    public bool BooleanLiterals { get; init; }

    public bool OrderByInSubqueryNeedsLimit { get; init; }

    public bool LimitInInSubquery { get; init; }

    public bool QuantifiedSubquery { get; init; }

    public bool CaseInsensitiveLike { get; init; }

    public bool UnquotedFoldsToLower { get; init; }

    public bool NestedWith { get; init; }

    public bool ParenthesizedSetOperands { get; init; }

    public bool IntersectBindsTighter { get; init; }

    public ValuesStyle Values { get; init; }

    public LimitStyle LimitStyle { get; init; }

    public ConcatStyle ConcatStyle { get; init; }

    public ReturningStyle Returning { get; init; }

    public UpsertStyle Upsert { get; init; }

    public UpdateFromStyle UpdateFrom { get; init; }

    public DeleteJoinStyle DeleteJoin { get; init; }
}
