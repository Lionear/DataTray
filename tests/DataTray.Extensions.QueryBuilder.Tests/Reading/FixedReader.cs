using DataTray.Extensions.QueryBuilder.Model;
using DataTray.Extensions.QueryBuilder.Reading;

namespace DataTray.Extensions.QueryBuilder.Tests.Reading;

// A reader that "reads" any text as the given statement, to stand in for a reader that maps lossily.
internal sealed class FixedReader(Statement statement) : ISqlReader
{
    public ReadResult Read(string sql) => new(ReadOutcome.Read, statement, [], 0, 0);
}
