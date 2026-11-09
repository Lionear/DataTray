namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>
/// A small tokenizer that finds what makes text a script rather than one query: separators, GO lines and
/// statement-starting keywords. It skips comments, strings and quoted identifiers, and counts the comments.
/// </summary>
internal static class ScriptScanner
{
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
            if ((c == '-' && next == '-') || (mysql && c == '#'))
            {
                comments++;
                i = LineEnd(sql, i);
                continue;
            }
            if (c == '/' && next == '*')
            {
                comments++;
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end >= 0 ? end + 2 : sql.Length;
                line += sql.AsSpan(i, stop - i).Count('\n');
                i = stop;
                continue;
            }
            if (c == ';')
            {
                Flush();
                atStart = true;
                i++;
                continue;
            }

            var tokenLine = line;
            string? word = null;
            if (c is '\'' || (c is '"' && !sqlServer) || (c is '`' && !sqlServer) || (c is '[' && providerId is "sqlserver" or "sqlite"))
            {
                var close = c == '[' ? ']' : c;
                i = SkipQuoted(sql, i + 1, close, mysql && c != '`', ref line);
            }
            else if (char.IsLetter(c) || c == '_' || c == '@')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '@' or '$'))
                {
                    i++;
                }
                word = sql[start..i].ToUpperInvariant();
                // SqlParserCS 0.6.5 misreads PostgreSQL E'…' strings (it invents an alias), so they are refused.
                if (providerId == "postgres" && word == "E" && i < sql.Length && sql[i] == '\'')
                {
                    reasons.Add(new ReadReason("escape-string", "PostgreSQL E'…' strings can't be read back yet.", tokenLine));
                    i = SkipQuoted(sql, i + 1, '\'', true, ref line);
                }
            }
            else
            {
                i++;
            }

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
                if (word is not null && word != "BEGIN" && word != "START" && Finding(word, null, sqlServer) is { } finding)
                {
                    reasons.Add(finding with { Line = tokenLine });
                    starter = null;
                }
            }
            else if (starter is not null)
            {
                if (Finding(starter, word, sqlServer) is { } finding)
                {
                    reasons.Add(finding with { Line = starterLine });
                }
                starter = null;
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
        "BEGIN" when !sqlServer || next is "TRAN" or "TRANSACTION" => Transaction(),
        "BEGIN" => ControlFlow(),
        "START" when next is "TRANSACTION" => Transaction(),
        "COMMIT" or "ROLLBACK" or "SAVEPOINT" => Transaction(),
        "CREATE" or "ALTER" or "DROP" or "TRUNCATE" => Reason("ddl", "Schema changes (CREATE, ALTER, DROP, TRUNCATE) can't be shown in the builder."),
        "EXEC" or "EXECUTE" or "CALL" => Reason("procedure-call", "Procedure calls can't be shown in the builder."),
        _ => null,
    };

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

    private static int LineEnd(string sql, int i) => sql.IndexOf('\n', i) is var end and >= 0 ? end : sql.Length;

    private static bool IsGoLine(string sql, int i)
    {
        var text = sql[i..LineEnd(sql, i)].Trim();
        return text.Equals("GO", StringComparison.OrdinalIgnoreCase)
            || (text.StartsWith("GO ", StringComparison.OrdinalIgnoreCase) && int.TryParse(text[3..].Trim(), out _));
    }
}
