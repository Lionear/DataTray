namespace DataTray.Extensions.QueryBuilder.Dialects;

public interface IDialect
{
    string Id { get; }

    DialectCapabilities Capabilities { get; }

    string QuoteIdentifier(string name);

    string StringLiteral(string value);

    string BooleanLiteral(bool value);
}
