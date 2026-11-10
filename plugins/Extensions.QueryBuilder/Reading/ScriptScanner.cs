namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>
/// A small tokenizer that finds what makes text a script rather than one query: separators, GO lines and
/// statement-starting keywords. It skips comments, strings and quoted identifiers, and counts the comments.
/// </summary>
internal static class ScriptScanner
{
    private static readonly string[] MySqlModifiers =
    [
        "DISTINCTROW", "SQL_CALC_FOUND_ROWS", "HIGH_PRIORITY", "STRAIGHT_JOIN", "SQL_SMALL_RESULT", "SQL_BIG_RESULT",
        "SQL_BUFFER_RESULT", "SQL_NO_CACHE", "SQL_CACHE",
    ];

    public static ScriptScan Scan(string sql, string providerId)
    {
        var mysql = providerId == "mysql";
        var sqlServer = providerId == "sqlserver";
        var reasons = new List<ReadReason>();
        var comments = 0;
        var statements = 0;
        var firstLine = 1;
        var atStart = true;
        string? starter = null;
        var starterLine = 0;
        var line = 1;
        var i = 0;
        var prev = string.Empty;
        var afterString = false;

        while (i < sql.Length)
        {
            if (sqlServer && (i == 0 || sql[i - 1] == '\n') && IsGoLine(sql, i))
            {
                Flush();
                atStart = true;
                i = LineEnd(sql, i);
                continue;
            }
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            // MySQL needs whitespace after "--" for a comment; "1 --1" is 1 - -1.
            var dashComment = c == '-' && next == '-' && (!mysql || i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2]));
            if (dashComment || (mysql && c == '#'))
            {
                comments++;
                i = LineEnd(sql, i);
                continue;
            }
            if (c == '/' && next == '*')
            {
                var stop = sqlServer ? NestedCommentEnd(sql, i) : sql.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : sql.Length;
                if (mysql && i + 2 < sql.Length && (sql[i + 2] is '!' or '+' || (sql[i + 2] == 'M' && i + 3 < sql.Length && sql[i + 3] == '!')))
                {
                    reasons.Add(new ReadReason("mysql-executable-comment", "MySQL runs /*! … */ and /*+ … */ comments (MariaDB also /*M! … */); the builder would drop them.", line));
                }
                else
                {
                    comments++;
                }
                line += sql.AsSpan(i, stop - i).Count('\n');
                i = stop;
                continue;
            }
            if (c == ';')
            {
                Flush();
                atStart = true;
                prev = ";";
                afterString = false;
                i++;
                continue;
            }

            var tokenLine = line;
            string? word = null;
            ReadReason? finding = null;
            var isString = false;
            if (c is '\'' || c is '"' || (c is '`' && !sqlServer) || (c is '[' && providerId is "sqlserver" or "sqlite"))
            {
                var start = i;
                var close = c == '[' ? ']' : c;
                i = SkipQuoted(sql, i + 1, close, mysql && c != '`', ref line);
                isString = c == '\'' || (mysql && c == '"');
                if (isString && afterString)
                {
                    finding = Reason("adjacent-strings", "Adjacent string literals are joined or read as an alias depending on the server; the builder can't keep that.") with { Line = tokenLine };
                }
                else if (isString && mysql && HasLikeEscape(sql, start + 1, i))
                {
                    finding = Reason("mysql-like-escape", "MySQL keeps \\% and \\_ in strings as written; the builder would read them differently.") with { Line = tokenLine };
                }
            }
            else if (char.IsLetter(c) || c == '_' || c == '@')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '@' or '$'))
                {
                    i++;
                }
                word = sql[start..i].ToUpperInvariant();
                var quoteAt = word == "U" && providerId == "postgres" && i + 1 < sql.Length && sql[i] == '&' ? i + 1 : i;
                if (quoteAt < sql.Length && sql[quoteAt] == '\'' && Prefix(word, providerId) is { } prefix)
                {
                    finding = prefix with { Line = tokenLine };
                    i = SkipQuoted(sql, quoteAt + 1, '\'', mysql || word == "E", ref line);
                    isString = true;
                }
                else if (WordFinding(word, prev, providerId) is { } wordFinding)
                {
                    finding = wordFinding with { Line = tokenLine };
                }
            }
            else if (mysql && ((c == '|' && next == '|') || (c == '-' && next == '-')))
            {
                finding = c == '|' ? MySqlOperator() with { Line = tokenLine } : Reason("mysql-double-dash", "In MySQL, -- without a following space is not a comment; the builder would read it as one.") with { Line = tokenLine };
                i += 2;
            }
            else if (providerId == "sqlite" && c == ':' && next == ':')
            {
                finding = Reason("sqlite-cast", "SQLite has no :: cast; the builder would read one.") with { Line = tokenLine };
                i += 2;
            }
            else
            {
                i++;
            }
            prev = word ?? c.ToString();
            afterString = isString;

            if (atStart)
            {
                atStart = false;
                statements++;
                if (statements == 1)
                {
                    firstLine = tokenLine;
                }
                if (statements == 2)
                {
                    reasons.Add(new ReadReason("multiple-statements", "The builder shows one statement at a time; this script has more than one.", tokenLine));
                }
                starter = word;
                starterLine = tokenLine;
                if (word is not null && word != "BEGIN" && word != "START" && Finding(word, null, sqlServer) is { } startFinding)
                {
                    reasons.Add(startFinding with { Line = tokenLine });
                    starter = null;
                }
            }
            else if (starter is not null)
            {
                if (Finding(starter, word, sqlServer) is { } starterFinding)
                {
                    reasons.Add(starterFinding with { Line = starterLine });
                }
                starter = null;
            }
            if (finding is not null)
            {
                reasons.Add(finding);
            }
        }
        Flush();
        return new ScriptScan(reasons, comments, statements, firstLine);

        // BEGIN and START need the next word; a script can end right after them.
        void Flush()
        {
            if (starter is not null && Finding(starter, null, sqlServer) is { } finding)
            {
                reasons.Add(finding with { Line = starterLine });
            }
            starter = null;
        }
    }

    private static ReadReason? Finding(string word, string? next, bool sqlServer) => word switch
    {
        "DECLARE" => Reason("declare", "Variable declarations can't be shown in the builder."),
        "SET" => Reason("set-variable", "SET statements can't be shown in the builder."),
        "IF" or "WHILE" => ControlFlow(),
        "BEGIN" when !sqlServer || next is "TRAN" or "TRANSACTION" or "DISTRIBUTED" => Transaction(),
        "BEGIN" => ControlFlow(),
        "START" when next is "TRANSACTION" => Transaction(),
        "COMMIT" or "ROLLBACK" or "SAVEPOINT" => Transaction(),
        "CREATE" or "ALTER" or "DROP" or "TRUNCATE" => Reason("ddl", "Schema changes (CREATE, ALTER, DROP, TRUNCATE) can't be shown in the builder."),
        "EXEC" or "EXECUTE" or "CALL" => Reason("procedure-call", "Procedure calls can't be shown in the builder."),
        _ => null,
    };

    // Tokens the parser accepts but reads with another meaning than the server; see AstMapper for the rule.
    private static ReadReason? Prefix(string word, string providerId) => (providerId, word) switch
    {
        ("postgres", "E") => Reason("escape-string", "PostgreSQL E'…' strings can't be read back yet."),
        ("postgres", "B" or "X" or "U") or ("mysql", "B" or "X") => PrefixedString(),
        ("mysql", _) when word.StartsWith('_') => PrefixedString(),
        _ => null,
    };

    private static ReadReason? WordFinding(string word, string prev, string providerId) => providerId switch
    {
        "mysql" or "sqlserver" when word.StartsWith('@') => Reason("variable", "Variables can't be shown in the builder."),
        "mysql" when word == "XOR" => MySqlOperator(),
        "mysql" when MySqlModifiers.Contains(word) => Reason("mysql-modifier", "MySQL SELECT modifiers can't be shown in the builder."),
        "postgres" when word == "ONLY" && prev is "FROM" or "JOIN" or "," => Reason("only", "ONLY (skip inherited tables) can't be shown in the builder."),
        _ => null,
    };

    private static ReadReason PrefixedString() => Reason("prefixed-string", "Prefixed string literals (B'…', X'…', U&'…', _charset'…') can't be read back yet.");

    private static ReadReason MySqlOperator() => Reason("mysql-operator", "MySQL || and XOR can't be read back with their MySQL meaning.");

    private static bool HasLikeEscape(string sql, int from, int to)
    {
        for (var j = from; j < to - 1; j++)
        {
            if (sql[j] == '\\')
            {
                if (sql[j + 1] is '%' or '_')
                {
                    return true;
                }
                j++;
            }
        }
        return false;
    }

    private static ReadReason ControlFlow() => Reason("control-flow", "Control flow (IF, WHILE, BEGIN … END) can't be shown in the builder.");

    private static ReadReason Transaction() => Reason("transaction", "Transaction statements can't be shown in the builder.");

    private static ReadReason Reason(string code, string message) => new(code, message, 0);

    // A doubled close character escapes itself; MySQL strings also take backslash escapes.
    private static int SkipQuoted(string sql, int i, char close, bool backslash, ref int line)
    {
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\n')
            {
                line++;
            }
            if (backslash && c == '\\')
            {
                line += i + 1 < sql.Length && sql[i + 1] == '\n' ? 1 : 0;
                i += 2;
                continue;
            }
            if (c == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close)
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return i;
    }

    // T-SQL block comments nest: "/* a /* b */ c */" is one comment.
    private static int NestedCommentEnd(string sql, int i)
    {
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

    private static int LineEnd(string sql, int i) => sql.IndexOf('\n', i) is var end and >= 0 ? end : sql.Length;

    private static bool IsGoLine(string sql, int i)
    {
        var text = sql[i..LineEnd(sql, i)].Trim();
        return text.Equals("GO", StringComparison.OrdinalIgnoreCase)
            || (text.StartsWith("GO ", StringComparison.OrdinalIgnoreCase) && int.TryParse(text[3..].Trim(), out _));
    }
}
