using SqlParser;
using SqlParser.Dialects;

namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>
/// SqlParserCS 0.6.5's PostgreSqlDialect ranks infix operators on a 10–140 scale but prefix operands on the base 10–50
/// scale, so "-a &lt; 5" read as "-(a &lt; 5)" and "NOT a = 1 AND b" as "NOT (a = 1 AND b)". These follow PostgreSQL's table.
/// </summary>
internal sealed class PostgresDialect : PostgreSqlDialect
{
    public override short GetPrecedence(Precedence precedence) => precedence switch
    {
        Precedence.MulDivModOp => 125,
        Precedence.PlusMinus => 80,
        Precedence.Like => 60,
        Precedence.UnaryNot => 30,
        _ => base.GetPrecedence(precedence),
    };
}
