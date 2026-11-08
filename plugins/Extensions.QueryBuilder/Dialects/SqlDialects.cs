namespace DataTray.Extensions.QueryBuilder.Dialects;

public static class SqlDialects
{
    public static IDialect For(string providerId, string? serverVersion)
    {
        var v = ServerVersion.Parse(serverVersion);
        return providerId switch
        {
            "sqlserver" => new Dialect("sqlserver", '[', ']', "N", false, SqlServer(v)),
            "postgres" => new Dialect("postgres", '"', '"', string.Empty, false, Postgres(v)),
            "mysql" => new Dialect("mysql", '`', '`', string.Empty, true, MySql(v)),
            "sqlite" => new Dialect("sqlite", '"', '"', string.Empty, false, Sqlite(v)),
            _ => throw new NotSupportedException($"Unknown provider '{providerId}'."),
        };
    }

    private static DialectCapabilities SqlServer(ServerVersion? v) => new()
    {
        QuantifiedSubquery = true,
        RightJoin = true,
        FullJoin = true,
        Apply = true,
        WindowFunctions = true,
        NamedWindow = ServerVersion.AtLeast(v, 16),
        IntersectExcept = true,
        Merge = true,
        MergeNotMatchedBySource = true,
        OrderByInSubqueryNeedsLimit = true,
        LimitInInSubquery = true,
        LimitStyle = LimitStyle.Top,
        ConcatStyle = ConcatStyle.ConcatFunction,
        Returning = ReturningStyle.Output,
        Upsert = UpsertStyle.None,
        UpdateFrom = UpdateFromStyle.FromJoin,
        DeleteJoin = DeleteJoinStyle.FromJoin,
    };

    private static DialectCapabilities Postgres(ServerVersion? v) => new()
    {
        QuantifiedSubquery = true,
        CaseInsensitiveLike = true,
        UnquotedFoldsToLower = true,
        RightJoin = true,
        FullJoin = true,
        Lateral = true,
        RecursiveKeyword = true,
        WindowFunctions = true,
        RangeWithOffset = true,
        GroupsFrame = true,
        FrameExclude = true,
        NamedWindow = true,
        IntersectExcept = true,
        IntersectExceptAll = true,
        Merge = ServerVersion.AtLeast(v, 15),
        MergeNotMatchedBySource = ServerVersion.AtLeast(v, 17),
        BooleanLiterals = true,
        LimitInInSubquery = true,
        LimitStyle = LimitStyle.Limit,
        ConcatStyle = ConcatStyle.Pipes,
        Returning = ReturningStyle.Returning,
        Upsert = UpsertStyle.OnConflict,
        UpdateFrom = UpdateFromStyle.From,
        DeleteJoin = DeleteJoinStyle.Using,
    };

    private static DialectCapabilities MySql(ServerVersion? v) => new()
    {
        QuantifiedSubquery = true,
        RightJoin = true,
        Lateral = ServerVersion.AtLeast(v, 8, 0, 14),
        RecursiveKeyword = true,
        WindowFunctions = true,
        RangeWithOffset = true,
        NamedWindow = true,
        IntersectExcept = ServerVersion.AtLeast(v, 8, 0, 31),
        IntersectExceptAll = ServerVersion.AtLeast(v, 8, 0, 31),
        BooleanLiterals = true,
        LimitStyle = LimitStyle.Limit,
        ConcatStyle = ConcatStyle.ConcatFunction,
        Returning = ReturningStyle.None,
        Upsert = UpsertStyle.OnDuplicateKey,
        UpdateFrom = UpdateFromStyle.JoinSet,
        DeleteJoin = DeleteJoinStyle.FromJoin,
    };

    private static DialectCapabilities Sqlite(ServerVersion? v) => new()
    {
        RightJoin = ServerVersion.AtLeast(v, 3, 39),
        FullJoin = ServerVersion.AtLeast(v, 3, 39),
        RecursiveKeyword = true,
        WindowFunctions = ServerVersion.AtLeast(v, 3, 25),
        RangeWithOffset = ServerVersion.AtLeast(v, 3, 28),
        GroupsFrame = ServerVersion.AtLeast(v, 3, 28),
        FrameExclude = ServerVersion.AtLeast(v, 3, 28),
        NamedWindow = ServerVersion.AtLeast(v, 3, 25),
        IntersectExcept = true,
        BooleanLiterals = ServerVersion.AtLeast(v, 3, 23),
        LimitInInSubquery = true,
        LimitStyle = LimitStyle.Limit,
        ConcatStyle = ConcatStyle.Pipes,
        Returning = ServerVersion.AtLeast(v, 3, 35) ? ReturningStyle.Returning : ReturningStyle.None,
        Upsert = ServerVersion.AtLeast(v, 3, 24) ? UpsertStyle.OnConflict : UpsertStyle.None,
        UpdateFrom = ServerVersion.AtLeast(v, 3, 33) ? UpdateFromStyle.From : UpdateFromStyle.None,
        DeleteJoin = DeleteJoinStyle.None,
    };
}
