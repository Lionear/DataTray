namespace DataTray.Extensions.QueryBuilder.Model;

public static class ValueList
{
    public static ValueList<T> Create<T>(ReadOnlySpan<T> items) => new(items.ToArray());
}
