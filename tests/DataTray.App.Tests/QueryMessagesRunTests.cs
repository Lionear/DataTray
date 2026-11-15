using DataTray.App.ViewModels;
using DataTray.Core.Settings;
using DataTray.Sdk;
using DataTray.Sdk.Branding;
using DataTray.Sdk.Connections;
using DataTray.Sdk.Ddl;
using DataTray.Sdk.Query;
using DataTray.Sdk.Schema;

namespace DataTray.App.Tests;

public class QueryMessagesRunTests
{
    private readonly FakeClock _clock = new();
    private readonly MessagingProvider _provider = new();
    private readonly AppSettings _settings = new() { PageQueryResults = false };

    [Fact] // SE-300: the ticket's batch-delete loop — each RAISERROR … WITH NOWAIT shows while the script still runs.
    public async Task Run_ShowsServerMessagesWhileTheScriptIsStillRunning()
    {
        var reached = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        _provider.Script = async (messages, _) =>
        {
            messages.Report(new QueryMessage(QueryMessageKind.Info, "Region 1  deleted total: 20000"));
            reached.SetResult();
            await gate.Task;
            return [Dml(20000)];
        };
        var document = NewDocument();

        var run = document.RunCommand.ExecuteAsync(null);
        await reached.Task;
        document.Tick();

        Assert.True(document.IsRunning);
        Assert.Equal(["Region 1  deleted total: 20000"], document.Messages.Visible.Select(e => e.Text));
        Assert.Equal(QueryRunOutcome.Running, document.RunOutcome);

        gate.SetResult();
        await run;
        Assert.Equal(QueryRunOutcome.Completed, document.RunOutcome);
    }

    [Theory] // SE-300: messages belong to the run that produced them — wiped when the next run starts, unless the
             // user keeps them, and then a divider separates the runs.
    [InlineData(false, 1, 1)]
    [InlineData(true, 2, 3)]
    public async Task NextRun_ClearsThePreviousMessages_UnlessKeepIsOn(bool keep, int total, int lines)
    {
        _settings.KeepQueryMessagesBetweenRuns = keep;
        _provider.Script = (messages, _) =>
        {
            messages.Report(new QueryMessage(QueryMessageKind.Info, "run"));
            return Task.FromResult<IReadOnlyList<QueryResult>>([Dml(1)]);
        };
        var document = NewDocument();

        await document.RunCommand.ExecuteAsync(null);
        await document.RunCommand.ExecuteAsync(null);

        Assert.Equal(total, document.Messages.Total);
        Assert.Equal(lines, document.Messages.Visible.Count);
    }

    [Fact] // SE-300: Stop keeps everything that arrived and says where it stopped; the clock freezes there.
    public async Task Stop_KeepsTheMessagesSoFarAndSaysItWasCancelled()
    {
        var reached = new TaskCompletionSource();
        _provider.Script = async (messages, ct) =>
        {
            messages.Report(new QueryMessage(QueryMessageKind.Info, "batch 1"));
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        };
        var document = NewDocument();

        var run = document.RunCommand.ExecuteAsync(null);
        await reached.Task;
        _clock.Advance(TimeSpan.FromSeconds(42));
        document.StopCommand.Execute(null);
        await run;

        Assert.Equal(["batch 1", "QueryCancelledAfter"], document.Messages.Visible.Select(e => e.Text));
        Assert.True(document.Messages.Visible[1].IsClient);
        Assert.Equal(QueryRunOutcome.Cancelled, document.RunOutcome);
        Assert.Equal("00:42", document.RunClock);
        Assert.True(document.ShowMessagesTab);
    }

    [Fact] // SE-300: a provider that reports its own error line (with the server header) does not get it twice,
           // and the tab shows the messages rather than the grid the failed run never replaced.
    public async Task Error_ReportedByTheProvider_StaysInTheTimelineOnce()
    {
        var document = NewDocument();
        _provider.Script = (_, _) => Task.FromResult<IReadOnlyList<QueryResult>>([Grid()]);
        await document.RunCommand.ExecuteAsync(null);
        _provider.Script = (messages, _) =>
        {
            messages.Report(new QueryMessage(QueryMessageKind.Info, "batch 1"));
            messages.Report(new QueryMessage(QueryMessageKind.Error, "conflict") { Code = "Msg 547" });
            throw new InvalidOperationException("conflict");
        };

        await document.RunCommand.ExecuteAsync(null);

        Assert.Equal(["batch 1", "conflict"], document.Messages.Visible.Select(e => e.Text));
        Assert.Equal(QueryRunOutcome.Failed, document.RunOutcome);
        Assert.Equal("Msg 547", document.Messages.FirstErrorCode);
        Assert.True(document.IsMessagesSelected);
    }

    [Fact] // SE-300: a provider that only throws still gets its error into the timeline, after what came before.
    public async Task Error_OnlyThrown_IsAddedToTheTimeline()
    {
        _provider.Script = (messages, _) =>
        {
            messages.Report(new QueryMessage(QueryMessageKind.Info, "batch 1"));
            throw new InvalidOperationException("boom");
        };
        var document = NewDocument();

        await document.RunCommand.ExecuteAsync(null);

        Assert.Equal(["batch 1", "boom"], document.Messages.Visible.Select(e => e.Text));
        Assert.Equal(1, document.Messages.ErrorCount);
    }

    public static IEnumerable<object[]> TabCases() =>
    [
        // supports, results, messages, seconds, shown, messages selected
        [true, new[] { Grid() }, Array.Empty<QueryMessage>(), 0, false, false],                       // a quick SELECT looks as it does now
        [true, new[] { Grid() }, new[] { Rows(2) }, 0, false, false],                                // a bare row count is not news
        [true, new[] { Grid() }, new[] { Info("PRINT") }, 0, true, false],                           // a real message: tab, grid stays
        [true, new[] { Dml(3) }, new[] { Info("PRINT") }, 0, true, true],                            // nothing to grid: Messages
        [true, new[] { Grid(), Grid() }, Array.Empty<QueryMessage>(), 0, true, false],               // several result sets
        [true, new[] { Grid() }, Array.Empty<QueryMessage>(), 2, true, false],                       // a run over a second
        [false, new[] { Dml(3) }, new[] { Info("PRINT") }, 2, false, false],                         // provider without messages
    ];

    [Theory, MemberData(nameof(TabCases))] // SE-300: the Messages tab only appears when there is something to say.
    public async Task MessagesTab_AppearsOnlyWhenItHasSomethingToSay(
        bool supports, QueryResult[] results, QueryMessage[] messages, int seconds, bool shown, bool messagesSelected)
    {
        _provider.Supports = supports;
        _provider.Script = (sink, _) =>
        {
            foreach (var message in messages)
            {
                sink.Report(message);
            }

            _clock.Advance(TimeSpan.FromSeconds(seconds));
            return Task.FromResult<IReadOnlyList<QueryResult>>(results);
        };
        var document = NewDocument();

        await document.RunCommand.ExecuteAsync(null);

        Assert.Equal(shown, document.ShowMessagesTab);
        Assert.Equal(messagesSelected, document.IsMessagesSelected);
    }

    [Fact] // SE-300: picking a tab by hand wins over the automatic choice at the end of the run.
    public async Task TabPickedDuringTheRun_StaysPicked()
    {
        var reached = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        _provider.Script = async (messages, _) =>
        {
            messages.Report(new QueryMessage(QueryMessageKind.Info, "PRINT"));
            reached.SetResult();
            await gate.Task;
            return [Grid()];
        };
        var document = NewDocument();

        var run = document.RunCommand.ExecuteAsync(null);
        await reached.Task;
        document.Tick();
        document.SelectMessagesCommand.Execute(null);
        gate.SetResult();
        await run;

        Assert.True(document.IsMessagesSelected);
    }

    private DocumentViewModel NewDocument()
    {
        var document = QueryDocumentFixture.NewDocument(_provider, _settings, _clock, out var connection);
        document.InitQuery(connection);
        document.Sql = "EXEC dbo.Purge";
        return document;
    }

    private static QueryMessage Info(string text) => new(QueryMessageKind.Info, text);

    private static QueryMessage Rows(int n) => new(QueryMessageKind.RowsAffected, $"({n} rows affected)");

    private static QueryResult Grid() => new()
    {
        Columns = [new ResultColumn("x", typeof(int))], Rows = [[1]], RecordsAffected = -1, Elapsed = TimeSpan.Zero,
    };

    private static QueryResult Dml(int rows) => new() { Columns = [], Rows = [], RecordsAffected = rows, Elapsed = TimeSpan.Zero };

    // A provider whose script the test writes; the plain overload stands in for an engine without messages.
    private sealed class MessagingProvider : IDbProvider
    {
        public bool Supports { get; set; } = true;

        public Func<IProgress<QueryMessage>, CancellationToken, Task<IReadOnlyList<QueryResult>>> Script { get; set; } =
            (_, _) => Task.FromResult<IReadOnlyList<QueryResult>>([]);

        public bool SupportsQueryMessages => Supports;

        public Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(
            ConnectionProfile profile, string sql, IProgress<QueryMessage> messages, CancellationToken ct) =>
            Supports ? Script(messages, ct) : ExecuteScriptAsync(profile, sql, ct);

        public Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct) =>
            Script(new Progress<QueryMessage>(), ct);

        public string DisplayName => "Fake DB";
        public ProviderIcon? Icon => null;
        public ISqlDialect Dialect => throw new NotSupportedException();
        public bool IsSqlBased => true;
        public IReadOnlyList<ConnectionField> ConnectionFields => [new ConnectionField("database", "Database", ConnectionFieldType.Text)];
        public string BuildConnectionString(IReadOnlyDictionary<string, string?> values) => "fake";
        public Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ExecuteBatchAsync(ConnectionProfile profile, IReadOnlyList<SqlStatement> statements, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<CreateCapability> CreateCapabilities => [];
        public IReadOnlyList<string> ColumnTypes => [];
        public SqlStatement BuildCreateStatement(CreateObjectSpec spec) => throw new NotSupportedException();
        public Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
    }
}
