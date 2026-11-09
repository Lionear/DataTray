namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>Thrown while mapping when no fragment can keep the meaning (a clause of a query); the whole read is refused.</summary>
internal sealed class Refused(string what) : Exception(what);
