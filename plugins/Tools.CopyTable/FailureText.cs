namespace SqlExplorer.Tools.CopyTable;

/// <summary>
/// Turns an engine's failure message into something a banner can show. Kept out of the view so it can be
/// tested without Avalonia (the plugin excludes Avalonia's runtime assets, so a type deriving from
/// <c>UserControl</c> can't even be loaded in a test process).
/// </summary>
public static class FailureText
{
    /// <summary>How many <i>distinct</i> lines are shown before the rest is summarised.</summary>
    public const int MaxDistinctLines = 8;

    /// <summary>
    /// Collapses a failure message that says the same thing many times. Rows are inserted in batches, so one
    /// bad column produces one complaint <i>per offending row</i> — SQL Server will happily hand back four
    /// hundred copies of "String or binary data would be truncated". Reading that four hundred times is no
    /// more informative than reading it once, and it buries the lines that actually differ.
    ///
    /// <para>Identical lines collapse to one carrying a count, in first-seen order, so the distinct failures
    /// stay visible and their weight is still on show. Past <see cref="MaxDistinctLines"/> kinds the rest is
    /// summarised rather than dropped: an error list that was truncated but looks complete is worse than a
    /// long one.</para>
    /// </summary>
    /// <param name="summarise">Renders the "and N more kinds, M lines" tail (kinds, lines) — passed in so the
    /// collapsing itself needs no localizer.</param>
    public static string? Consolidate(string? message, Func<int, int, string> summarise)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var raw in message.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (counts.TryGetValue(line, out var seen))
            {
                counts[line] = seen + 1;
            }
            else
            {
                counts[line] = 1;
                order.Add(line);
            }
        }

        if (order.Count == 0)
        {
            return message;
        }

        var text = string.Join("\n", order
            .Take(MaxDistinctLines)
            .Select(line => counts[line] > 1 ? $"{line}  (×{counts[line]:N0})" : line));

        if (order.Count > MaxDistinctLines)
        {
            text += "\n" + summarise(order.Count - MaxDistinctLines, order.Skip(MaxDistinctLines).Sum(l => counts[l]));
        }

        return text;
    }
}
