namespace DataTray.Extensions.QueryBuilder.Dialects;

// One shape for all four providers; they differ only in quoting and capabilities.
internal sealed record Dialect(
    string Id,
    char QuoteOpen,
    char QuoteClose,
    string StringPrefix,
    bool EscapeBackslash,
    DialectCapabilities Capabilities) : IDialect
{
    public string QuoteIdentifier(string name)
    {
        return QuoteOpen + name.Replace(QuoteClose.ToString(), new string(QuoteClose, 2)) + QuoteClose;
    }

    public string StringLiteral(string value, bool narrow = false)
    {
        var text = EscapeBackslash ? value.Replace("\\", "\\\\") : value;
        return (narrow ? string.Empty : StringPrefix) + "'" + text.Replace("'", "''") + "'";
    }

    public string BooleanLiteral(bool value)
    {
        return Capabilities.BooleanLiterals ? (value ? "TRUE" : "FALSE") : (value ? "1" : "0");
    }
}
