using System.Text;
using DataTray.Extensions.QueryBuilder.Dialects;
using DataTray.Extensions.QueryBuilder.Generation;

namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>
/// The tokens of a query as the server sees them: no comments or whitespace, identifiers unquoted, strings by value and
/// kind, plus a short list of spellings that cannot change the meaning. Anything not listed stays a difference.
/// </summary>
public static class SqlTokens
{
    private static readonly string[] Operators = ["->>", "#>>", "<>", "!=", "<=", ">=", "||", "::", "->", "#>", "@>", "<@", "!<", "!>"];

    public static IReadOnlyList<string> Normalize(string sql, IDialect dialect) => Tokens(sql, dialect).Select(t => t.Text).ToArray();

    // Only words take part in the equivalences; Line is 1-based.
    internal static List<(string Text, int Line, SqlTokenKind Kind)> Tokens(string sql, IDialect dialect) =>
        Equivalences(Scan(sql, dialect.Id), dialect.Id);

    private static List<(string Text, int Line, SqlTokenKind Kind)> Scan(string sql, string id)
    {
        var mysql = id == "mysql";
        var postgres = id == "postgres";
        var sqlServer = id == "sqlserver";
        var tokens = new List<(string Text, int Line, SqlTokenKind Kind)>();
        var line = 1;
        var i = 0;
        while (i < sql.Length)
        {
            var start = i;
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            // MySQL needs whitespace after "--" for a comment, as in ScriptScanner.
            var dashComment = c == '-' && next == '-' && (!mysql || i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2]));
            if (sqlServer ? char.IsWhiteSpace(c) : c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v')
            {
                i++;
            }
            else if (dashComment || (mysql && c == '#'))
            {
                // PostgreSQL ends a line comment at \r as well; the others at \n only.
                i = (postgres ? sql.IndexOfAny(['\n', '\r'], i) : sql.IndexOf('\n', i)) is var end and >= 0 ? end : sql.Length;
            }
            else if (c == '/' && next == '*')
            {
                i = CommentEnd(sql, i, sqlServer || postgres);
            }
            else if (c == '\'' || (mysql && c == '"'))
            {
                (var value, i) = Quoted(sql, i + 1, c, mysql);
                tokens.Add(("'" + value + "'", line, SqlTokenKind.String));
            }
            else if (sqlServer && c is 'N' or 'n' && next == '\'')
            {
                (var value, i) = Quoted(sql, i + 2, '\'', false);
                tokens.Add(("N'" + value + "'", line, SqlTokenKind.String));
            }
            else if (c == '"' || (c == '`' && !sqlServer) || (c == '[' && (sqlServer || id == "sqlite")))
            {
                (var value, i) = Quoted(sql, i + 1, c == '[' ? ']' : c, false);
                tokens.Add((Identifier(value, postgres), line, SqlTokenKind.Name));
            }
            else if (IsWordChar(c, sqlServer) && !char.IsAsciiDigit(c) && c is not '$' and not '@')
            {
                while (i < sql.Length && IsWordChar(sql[i], sqlServer))
                {
                    i++;
                }
                tokens.Add((postgres ? FoldAscii(sql[start..i]) : sql[start..i].ToUpperInvariant(), line, SqlTokenKind.Word));
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(next)))
            {
                i = NumberEnd(sql, i);
                // Outside T-SQL a number glued to letters, digits or _ is one token (1_000, 0b101, 1a), never 1 AS a.
                while (!sqlServer && i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '_'))
                {
                    i++;
                }
                tokens.Add((sql[start..i], line, SqlTokenKind.Number));
            }
            else
            {
                var op = Operators.FirstOrDefault(o => string.CompareOrdinal(sql, i, o, 0, o.Length) == 0) ?? c.ToString();
                i += op.Length;
                tokens.Add((op, line, SqlTokenKind.Symbol));
            }
            line += LineBreaks(sql, start, i);
        }
        return tokens;
    }

    // The optional spellings: AS before an alias (not in CAST(x AS t), a CTE or a window), OUTER/INNER in joins, ASC, != for <>, a trailing ";", and per dialect
    // MySQL LIMIT o, n = LIMIT n OFFSET o, PostgreSQL LIMIT ALL = no limit, T-SQL TOP (n) = TOP n.
    private static List<(string Text, int Line, SqlTokenKind Kind)> Equivalences(List<(string Text, int Line, SqlTokenKind Kind)> t, string id)
    {
        var result = new List<(string Text, int Line, SqlTokenKind Kind)>();
        var functionParens = new Stack<bool>();
        for (var k = 0; k < t.Count; k++)
        {
            bool Is(int at, string text) => at >= 0 && at < t.Count && t[at].Text.Equals(text, StringComparison.OrdinalIgnoreCase)
                && t[at].Kind == (char.IsLetter(text[0]) ? SqlTokenKind.Word : SqlTokenKind.Symbol);
            bool IsNumber(int at) => at < t.Count && t[at].Kind == SqlTokenKind.Number;
            bool IsName(int at) => at < t.Count && (t[at].Kind == SqlTokenKind.Name || (t[at].Kind == SqlTokenKind.Word && !ReservedWords.Contains(t[at].Text)));
            bool EndsOperand(int at) => at >= 0 && (IsName(at) || t[at].Kind is SqlTokenKind.String or SqlTokenKind.Number
                || Is(at, ")") || Is(at, "END") || Is(at, "NULL") || Is(at, "TRUE") || Is(at, "FALSE"));

            if (Is(k, "("))
            {
                functionParens.Push(k > 0 && (IsName(k - 1) || Is(k - 1, "CAST") || Is(k - 1, "TRY_CAST") || Is(k - 1, "CONVERT")));
            }
            else if (Is(k, ")") && functionParens.Count > 0)
            {
                functionParens.Pop();
            }

            var alias = EndsOperand(k - 1) && IsName(k + 1) && !(functionParens.TryPeek(out var inFunction) && inFunction);
            if ((Is(k, "AS") && alias)
                || (Is(k, "OUTER") && (Is(k - 1, "LEFT") || Is(k - 1, "RIGHT") || Is(k - 1, "FULL")) && Is(k + 1, "JOIN"))
                || (Is(k, "INNER") && Is(k + 1, "JOIN"))
                || Is(k, "ASC")
                || (Is(k, ";") && k == t.Count - 1))
            {
                continue;
            }
            if (id == "postgres" && Is(k, "LIMIT") && Is(k + 1, "ALL"))
            {
                k++;
                continue;
            }
            if (id == "mysql" && Is(k, "LIMIT") && IsNumber(k + 1) && Is(k + 2, ",") && IsNumber(k + 3))
            {
                result.AddRange([t[k], t[k + 3], ("OFFSET", t[k].Line, SqlTokenKind.Word), t[k + 1]]);
                k += 3;
                continue;
            }
            if (id == "sqlserver" && Is(k, "TOP") && Is(k + 1, "(") && IsNumber(k + 2) && Is(k + 3, ")"))
            {
                result.AddRange([t[k], t[k + 2]]);
                k += 3;
                continue;
            }
            result.Add(Is(k, "!=") ? t[k] with { Text = "<>" } : t[k]);
        }
        return result;
    }

    // A quoted name that could be written bare compares as the bare word; a reserved or irregular one stays quoted so a
    // keyword never equals a name. PostgreSQL keeps the case of a quoted name; the others compare names case-insensitively.
    private static string Identifier(string value, bool postgres)
    {
        var text = postgres ? value : value.ToUpperInvariant();
        var regular = value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') && value.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
        return regular && !ReservedWords.Contains(value) ? text : "\"" + text + "\"";
    }

    // T-SQL words are Unicode letters and digits (as ScriptDom); the others take any non-ASCII character as a letter.
    private static bool IsWordChar(char c, bool sqlServer) =>
        c is '_' or '$' or '@' || (sqlServer ? char.IsLetterOrDigit(c) : char.IsAsciiLetterOrDigit(c) || c >= '\u0080');

    // \r\n, a lone \r and \n each end a line.
    private static int LineBreaks(string sql, int from, int to)
    {
        var count = 0;
        for (var j = from; j < to; j++)
        {
            count += sql[j] == '\n' || (sql[j] == '\r' && (j + 1 >= sql.Length || sql[j + 1] != '\n')) ? 1 : 0;
        }
        return count;
    }

    // PostgreSQL folds unquoted names to lower case, ASCII letters only (as AstMapper.Name).
    private static string FoldAscii(string word) => string.Concat(word.Select(ch => ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch));

    // A doubled close character escapes itself; MySQL strings also take backslash escapes.
    private static (string Value, int End) Quoted(string sql, int i, char close, bool backslash)
    {
        var value = new StringBuilder();
        while (i < sql.Length)
        {
            var c = sql[i];
            if (backslash && c == '\\' && i + 1 < sql.Length)
            {
                value.Append(MySqlEscape(sql[i + 1]));
                i += 2;
                continue;
            }
            if (c == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close)
                {
                    value.Append(close);
                    i += 2;
                    continue;
                }
                return (value.ToString(), i + 1);
            }
            value.Append(c);
            i++;
        }
        return (value.ToString(), i);
    }

    // MySQL keeps the backslash of \% and \_ (they are LIKE escapes); an unknown escape is the character itself.
    private static string MySqlEscape(char c) => c switch
    {
        '0' => "\0",
        'b' => "\b",
        'n' => "\n",
        'r' => "\r",
        't' => "\t",
        'Z' => "\x1A",
        '%' or '_' => "\\" + c,
        _ => c.ToString(),
    };

    private static int NumberEnd(string sql, int i)
    {
        while (i < sql.Length && (char.IsAsciiDigit(sql[i]) || sql[i] == '.'))
        {
            i++;
        }
        var exponent = i < sql.Length && sql[i] is 'e' or 'E' ? i + 1 : -1;
        if (exponent > 0 && exponent < sql.Length && sql[exponent] is '+' or '-')
        {
            exponent++;
        }
        if (exponent > 0 && exponent < sql.Length && char.IsAsciiDigit(sql[exponent]))
        {
            i = exponent;
            while (i < sql.Length && char.IsAsciiDigit(sql[i]))
            {
                i++;
            }
        }
        return i;
    }

    // T-SQL and PostgreSQL block comments nest: "/* a /* b */ c */" is one comment.
    private static int CommentEnd(string sql, int i, bool nested)
    {
        if (!nested)
        {
            return sql.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : sql.Length;
        }
        var depth = 0;
        while (i < sql.Length - 1)
        {
            var pair = sql.AsSpan(i, 2);
            depth += pair is "/*" ? 1 : pair is "*/" ? -1 : 0;
            i += pair is "/*" or "*/" ? 2 : 1;
            if (depth == 0)
            {
                return i;
            }
        }
        return sql.Length;
    }
}
