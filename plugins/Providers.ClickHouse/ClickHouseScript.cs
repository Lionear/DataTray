namespace DataTray.Providers.ClickHouse;

/// <summary>
/// Splits script text into individual statements, because ClickHouse's HTTP interface rejects more than
/// one statement per request outright — <c>SELECT 1; SELECT 2;</c> comes back as
/// <c>Code: 62 … (Multi-statements are not allowed)</c>. Every other bundled engine lets its driver walk
/// the batch via <c>NextResult</c>; here <see cref="ClickHouseProvider.ExecuteScriptAsync"/> has to send
/// one request per statement, so it needs this first.
/// </summary>
/// <remarks>
/// The host already owns a richer splitter (<c>DataTray.Core.Sql.SqlStatementSplitter</c>), but a plugin
/// may reference only <c>DataTray.Sdk</c> — deliberately, so third-party providers depend on the public
/// contract alone. Hence this small local copy of the same idea, minus the pieces ClickHouse has no use
/// for (Postgres dollar-quoting, SQL Server GO batches) and plus the ones it does: backslash escapes
/// inside string literals, backtick identifiers, and <c>#</c> line comments.
/// <para>Public (unlike the Redis plugin's equivalent helpers, which stay internal) so
/// <c>ClickHouseScriptTests</c> can pin the splitting rules directly. It is not part of any contract —
/// the plugin's only public surface that matters is <see cref="ClickHouseProvider"/>.</para>
/// </remarks>
public static class ClickHouseScript
{
    public static IReadOnlyList<string> Split(string text)
    {
        var statements = new List<string>();
        var state = ScanState.Normal;
        var start = 0;

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            switch (state)
            {
                case ScanState.Normal:
                    if (c == '\'')
                    {
                        state = ScanState.SingleQuote;
                    }
                    else if (c == '"')
                    {
                        state = ScanState.DoubleQuote;
                    }
                    else if (c == '`')
                    {
                        state = ScanState.Backtick;
                    }
                    else if ((c == '-' && Peek(text, i + 1) == '-') || c == '#')
                    {
                        state = ScanState.LineComment;
                    }
                    else if (c == '/' && Peek(text, i + 1) == '*')
                    {
                        state = ScanState.BlockComment;
                        i++;
                    }
                    else if (c == ';')
                    {
                        Add(statements, text, start, i);
                        start = i + 1;
                    }

                    break;

                // ClickHouse string literals escape with a backslash (\' and \\), unlike the doubling the
                // host's splitter assumes — skipping the escaped character keeps a ';' inside 'a\'b;c' from
                // ending the statement.
                case ScanState.SingleQuote:
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '\'')
                    {
                        state = ScanState.Normal;
                    }

                    break;

                case ScanState.DoubleQuote:
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        state = ScanState.Normal;
                    }

                    break;

                case ScanState.Backtick:
                    if (c == '`')
                    {
                        state = ScanState.Normal;
                    }

                    break;

                case ScanState.LineComment:
                    if (c == '\n')
                    {
                        state = ScanState.Normal;
                    }

                    break;

                case ScanState.BlockComment:
                    if (c == '*' && Peek(text, i + 1) == '/')
                    {
                        state = ScanState.Normal;
                        i++;
                    }

                    break;
            }

            i++;
        }

        Add(statements, text, start, text.Length);
        return statements;
    }

    private static void Add(List<string> statements, string text, int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        var trimmed = text[start..end].Trim();
        if (trimmed.Length > 0)
        {
            statements.Add(trimmed);
        }
    }

    private static char Peek(string text, int index) => index < text.Length ? text[index] : '\0';

    private enum ScanState
    {
        Normal,
        SingleQuote,
        DoubleQuote,
        Backtick,
        LineComment,
        BlockComment
    }
}
