using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>Reads T-SQL through ScriptDom's TSql160Parser (QUOTED_IDENTIFIER on) after the script pre-scan.</summary>
internal sealed class ScriptDomReader : ISqlReader
{
    public ReadResult Read(string sql)
    {
        var scan = ScriptScanner.Scan(sql, "sqlserver");
        if (scan.StatementCount == 0)
        {
            return CannotShow(scan, new ReadReason("empty", "There is no statement to show.", 1));
        }
        if (scan.Reasons.Count > 0)
        {
            return CannotShow(scan, [.. scan.Reasons]);
        }

        using var reader = new StringReader(sql);
        var script = (TSqlScript)new TSql160Parser(true).Parse(reader, out var errors);
        if (errors is [var error, ..])
        {
            return CannotShow(scan, new ReadReason("parse-error", error.Message, error.Line > 0 ? error.Line : scan.FirstLine));
        }

        var statements = script.Batches.SelectMany(b => b.Statements).ToArray();
        if (statements is not [var statement])
        {
            return CannotShow(scan, new ReadReason("multiple-statements", "The builder shows one statement at a time; this script has more than one.", scan.FirstLine));
        }
        if (statement is not SelectStatement select)
        {
            return statement is InsertStatement or UpdateStatement or DeleteStatement or MergeStatement
                ? CannotShow(scan, new ReadReason("dml-not-yet", "INSERT, UPDATE, DELETE and MERGE can't be shown in the builder yet.", scan.FirstLine))
                : CannotShow(scan, new ReadReason("not-a-query", "Only queries can be shown in the builder.", scan.FirstLine));
        }

        var mapper = new ScriptDomMapper(sql);
        try
        {
            var query = mapper.Statement(select);
            var outcome = mapper.FragmentCount > 0 ? ReadOutcome.ReadWithFragments : ReadOutcome.Read;
            return new ReadResult(outcome, new Model.QueryStatement(query), [], mapper.FragmentCount, scan.CommentCount);
        }
        catch (Exception e) when (e is Unrepresentable or Refused)
        {
            var line = e is Refused { Line: > 0 } r ? r.Line : scan.FirstLine;
            return CannotShow(scan, new ReadReason("cannot-represent", $"The builder has no place for {e.Message}.", line));
        }
    }

    private static ReadResult CannotShow(ScriptScan scan, params ReadReason[] reasons) =>
        new(ReadOutcome.CannotShow, null, reasons, 0, scan.CommentCount);
}
