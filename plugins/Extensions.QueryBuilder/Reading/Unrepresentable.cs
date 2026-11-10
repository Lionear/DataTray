namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>Thrown while mapping when the model has no place for a part; the nearest expression or source keeps it as text.</summary>
internal sealed class Unrepresentable(string what) : Exception(what);
