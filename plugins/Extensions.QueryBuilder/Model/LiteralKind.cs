namespace DataTray.Extensions.QueryBuilder.Model;

public enum LiteralKind
{
    String,
    Number,
    Boolean,
    Null,
    // A T-SQL '…' (varchar) literal; String is N'…' there. Every other dialect writes both the same way.
    NarrowString,
}
