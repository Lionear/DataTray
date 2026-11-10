namespace DataTray.Extensions.QueryBuilder.Dialects;

public interface IDialect
{
    string Id { get; }

    DialectCapabilities Capabilities { get; }

    string QuoteIdentifier(string name);

    // Narrow drops the SQL Server N prefix ('…' is varchar there); the other dialects have none.
    string StringLiteral(string value, bool narrow = false);

    string BooleanLiteral(bool value);
}
