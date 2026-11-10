using System.Text;

namespace DataTray.Extensions.QueryBuilder.Generation;

// Builds the SQL text: one clause per line, nested queries two spaces deeper than the line that opens them.
internal sealed class SqlWriter
{
    private readonly StringBuilder _sql = new();
    private int _indent;
    private int _lineStart;

    public void Write(string text)
    {
        _sql.Append(text);
    }

    public void NewLine(int align = 0)
    {
        _sql.Append('\n');
        _lineStart = _sql.Length;
        _sql.Append(' ', _indent + align);
    }

    public void Nested(Action body)
    {
        var outer = _indent;
        var line = CurrentLineIndent();
        _sql.Append('(');
        _indent = line + 2;
        NewLine();
        body();
        _indent = line;
        NewLine();
        _sql.Append(')');
        _indent = outer;
    }

    public override string ToString()
    {
        return _sql.ToString();
    }

    private int CurrentLineIndent()
    {
        var i = _lineStart;
        while (i < _sql.Length && _sql[i] == ' ')
        {
            i++;
        }

        return i - _lineStart;
    }
}
