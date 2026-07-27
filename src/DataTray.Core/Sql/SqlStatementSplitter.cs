namespace DataTray.Core.Sql;

/// <summary>One statement's span in the original text (untrimmed offsets) plus its trimmed text.</summary>
public readonly record struct SqlStatementSpan(int Start, int End, string Text);

/// <summary>
/// Pure, dialect-agnostic splitter used by "execute at cursor" (find the statement under the caret)
/// and — separately, host-side — to break a script into SQL Server GO-batches before sending each to
/// <c>IDbProvider.ExecuteScriptAsync</c> (GO is not real T-SQL, just a client-side batch separator, so
/// it must never reach the driver).
/// </summary>
/// <remarks>
/// Splits on top-level <c>;</c> (aware of <c>'...'</c>/<c>"..."</c> strings, <c>--</c> line comments,
/// <c>/*...*/</c> block comments, and Postgres <c>$$...$$</c>/<c>$tag$...$tag$</c> dollar-quoting) and
/// on a standalone <c>GO</c> line (case-insensitive, optionally followed by a repeat count, alone on its
/// line). The GO-line check is line-based, not quote-aware — a table/alias literally named <c>GO</c> on
/// its own line is a known, accepted edge case (same class of limitation as the FROM/JOIN-regex used by
/// autocomplete).
/// </remarks>
public static class SqlStatementSplitter
{
    public static IReadOnlyList<SqlStatementSpan> Split(string text)
    {
        var spans = new List<SqlStatementSpan>();
        var state = ScanState.Normal;
        var statementStart = 0;
        string? dollarTag = null;

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            switch (state)
            {
                case ScanState.Normal:
                    if (i == 0 || text[i - 1] == '\n')
                    {
                        var lineEnd = text.IndexOf('\n', i);
                        var line = lineEnd < 0 ? text[i..] : text[i..lineEnd];
                        if (IsGoLine(line))
                        {
                            AddSpan(spans, text, statementStart, i);
                            i = lineEnd < 0 ? text.Length : lineEnd + 1;
                            statementStart = i;
                            continue;
                        }
                    }

                    if (c == '\'')
                    {
                        state = ScanState.SingleQuote;
                    }
                    else if (c == '"')
                    {
                        state = ScanState.DoubleQuote;
                    }
                    else if (c == '-' && Peek(text, i + 1) == '-')
                    {
                        state = ScanState.LineComment;
                        i++;
                    }
                    else if (c == '/' && Peek(text, i + 1) == '*')
                    {
                        state = ScanState.BlockComment;
                        i++;
                    }
                    else if (c == '$' && TryReadDollarTag(text, i, out var tag, out var tagLength))
                    {
                        dollarTag = tag;
                        state = ScanState.DollarQuote;
                        i += tagLength - 1;
                    }
                    else if (c == ';')
                    {
                        AddSpan(spans, text, statementStart, i + 1);
                        statementStart = i + 1;
                    }

                    break;

                case ScanState.SingleQuote:
                    if (c == '\'')
                    {
                        state = ScanState.Normal;
                    }

                    break;

                case ScanState.DoubleQuote:
                    if (c == '"')
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

                case ScanState.DollarQuote:
                    if (c == '$' && text.AsSpan(i).StartsWith(dollarTag!))
                    {
                        state = ScanState.Normal;
                        i += dollarTag!.Length - 1;
                    }

                    break;
            }

            i++;
        }

        AddSpan(spans, text, statementStart, text.Length);
        return spans;
    }

    /// <summary>
    /// Split raw script text into SQL Server GO-batches only (semicolons are left untouched — each
    /// batch may itself contain several ;-separated statements, which <c>ExecuteScriptAsync</c> reads
    /// via <c>NextResult</c> natively). Used host-side before sending text to a <c>sqlserver</c>
    /// connection, since <c>GO</c> is a client-side batch separator the driver would reject as invalid
    /// T-SQL if it were sent as-is.
    /// </summary>
    public static IReadOnlyList<string> SplitGoBatches(string text)
    {
        var batches = new List<string>();
        var state = ScanState.Normal;
        var batchStart = 0;
        string? dollarTag = null;

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (state == ScanState.Normal && (i == 0 || text[i - 1] == '\n'))
            {
                var lineEnd = text.IndexOf('\n', i);
                var line = lineEnd < 0 ? text[i..] : text[i..lineEnd];
                if (IsGoLine(line))
                {
                    AddBatch(batches, text, batchStart, i);
                    i = lineEnd < 0 ? text.Length : lineEnd + 1;
                    batchStart = i;
                    continue;
                }
            }

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
                    else if (c == '-' && Peek(text, i + 1) == '-')
                    {
                        state = ScanState.LineComment;
                        i++;
                    }
                    else if (c == '/' && Peek(text, i + 1) == '*')
                    {
                        state = ScanState.BlockComment;
                        i++;
                    }
                    else if (c == '$' && TryReadDollarTag(text, i, out var tag, out var tagLength))
                    {
                        dollarTag = tag;
                        state = ScanState.DollarQuote;
                        i += tagLength - 1;
                    }

                    break;

                case ScanState.SingleQuote:
                    if (c == '\'')
                    {
                        state = ScanState.Normal;
                    }

                    break;

                case ScanState.DoubleQuote:
                    if (c == '"')
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

                case ScanState.DollarQuote:
                    if (c == '$' && text.AsSpan(i).StartsWith(dollarTag!))
                    {
                        state = ScanState.Normal;
                        i += dollarTag!.Length - 1;
                    }

                    break;
            }

            i++;
        }

        AddBatch(batches, text, batchStart, text.Length);
        return batches;
    }

    private static void AddBatch(List<string> batches, string text, int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        var trimmed = text[start..end].Trim();
        if (trimmed.Length > 0)
        {
            batches.Add(trimmed);
        }
    }

    /// <summary>The statement whose span contains <paramref name="caret"/>, or null if the caret sits
    /// only in whitespace between statements (no span text).</summary>
    public static string? StatementAtCursor(string text, int caret)
    {
        var spans = Split(text);
        foreach (var span in spans)
        {
            if (caret >= span.Start && caret <= span.End && span.Text.Length > 0)
            {
                return span.Text;
            }
        }

        return spans.LastOrDefault(s => s.Text.Length > 0).Text;
    }

    private static void AddSpan(List<SqlStatementSpan> spans, string text, int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        var trimmed = text[start..end].Trim();
        if (trimmed.Length > 0)
        {
            spans.Add(new SqlStatementSpan(start, end, trimmed));
        }
    }

    private static char Peek(string text, int index) => index < text.Length ? text[index] : '\0';

    // "$$" or "$tag$" opens a dollar-quoted string; returns the full tag (including both $) and its length.
    private static bool TryReadDollarTag(string text, int start, out string tag, out int length)
    {
        var end = text.IndexOf('$', start + 1);
        if (end < 0)
        {
            tag = "";
            length = 0;
            return false;
        }

        for (var j = start + 1; j < end; j++)
        {
            if (!char.IsLetterOrDigit(text[j]) && text[j] != '_')
            {
                tag = "";
                length = 0;
                return false;
            }
        }

        tag = text[start..(end + 1)];
        length = tag.Length;
        return true;
    }

    private static bool IsGoLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || !trimmed.StartsWith("GO", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = trimmed[2..].Trim();
        return rest.Length == 0 || rest.All(char.IsDigit);
    }

    private enum ScanState
    {
        Normal,
        SingleQuote,
        DoubleQuote,
        LineComment,
        BlockComment,
        DollarQuote
    }
}
