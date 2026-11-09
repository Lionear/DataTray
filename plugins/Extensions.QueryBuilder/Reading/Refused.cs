namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>Thrown while mapping when no fragment can keep the meaning (a clause of a query); the whole read is refused.</summary>
internal sealed class Refused(string what, int line = 0) : Exception(what)
{
    /// <summary>1-based line of the refused part, or 0 when the reader doesn't know it.</summary>
    public int Line { get; } = line;
}
