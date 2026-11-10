namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>What a normalized token was in the text: an unquoted word (keyword or bare name), a quoted name, a string, a number or a symbol.</summary>
internal enum SqlTokenKind
{
    Word,
    Name,
    String,
    Number,
    Symbol,
}
