namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record Frame(FrameUnit Unit, FrameBound Start, FrameBound? End, FrameExclude? Exclude);
