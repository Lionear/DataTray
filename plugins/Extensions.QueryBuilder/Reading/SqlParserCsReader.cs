using SqlParser;
using SqlParser.Dialects;
using Ast = SqlParser.Ast;

namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>Reads PostgreSQL, MySQL and SQLite through SqlParserCS after the script pre-scan.</summary>
internal sealed class SqlParserCsReader(string providerId) : ISqlReader
{
    public ReadResult Read(string sql)
    {
        var scan = ScriptScanner.Scan(sql, providerId);
        if (scan.StatementCount == 0)
        {
            return CannotShow(scan, new ReadReason("empty", "There is no statement to show.", 1));
        }
        if (scan.Reasons.Count > 0)
        {
            return CannotShow(scan, [.. scan.Reasons]);
        }

        Sequence<Ast.Statement> statements;
        try
        {
            statements = new Parser().ParseSql(sql, Dialect());
        }
        catch (Exception e) when (e is ParserException or TokenizeException)
        {
            var line = e is ParserException p ? p.Line : ((TokenizeException)e).Line;
            return CannotShow(scan, new ReadReason("parse-error", e.Message, line > 0 ? (int)line : scan.FirstLine));
        }

        if (statements is not [var statement])
        {
            return CannotShow(scan, new ReadReason("multiple-statements", "The builder shows one statement at a time; this script has more than one.", scan.FirstLine));
        }
        if (statement is not Ast.Statement.Select select)
        {
            return statement is Ast.Statement.Insert or Ast.Statement.Update or Ast.Statement.Delete or Ast.Statement.Merge
                ? CannotShow(scan, new ReadReason("dml-not-yet", "INSERT, UPDATE, DELETE and MERGE can't be shown in the builder yet.", scan.FirstLine))
                : CannotShow(scan, new ReadReason("not-a-query", "Only queries can be shown in the builder.", scan.FirstLine));
        }

        var mapper = new AstMapper(providerId);
        try
        {
            var query = mapper.Query(select.Query);
            var outcome = mapper.FragmentCount > 0 ? ReadOutcome.ReadWithFragments : ReadOutcome.Read;
            return new ReadResult(outcome, new Model.QueryStatement(query), [], mapper.FragmentCount, scan.CommentCount);
        }
        catch (Unrepresentable e)
        {
            return CannotShow(scan, new ReadReason("cannot-represent", $"The builder has no place for {e.Message}.", scan.FirstLine));
        }
    }

    private Dialect Dialect() => providerId switch
    {
        "postgres" => new PostgresDialect(),
        "mysql" => new MySqlDialect(),
        _ => new SQLiteDialect(),
    };

    private static ReadResult CannotShow(ScriptScan scan, params ReadReason[] reasons) =>
        new(ReadOutcome.CannotShow, null, reasons, 0, scan.CommentCount);
}
