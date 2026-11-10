namespace DataTray.Extensions.QueryBuilder.Model;

public sealed record MergeUpdate(ValueList<Assignment> Set) : MergeAction;
