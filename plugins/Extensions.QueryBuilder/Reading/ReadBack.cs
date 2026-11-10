using DataTray.Extensions.QueryBuilder.Dialects;
using DataTray.Extensions.QueryBuilder.Generation;

namespace DataTray.Extensions.QueryBuilder.Reading;

/// <summary>
/// Reads a query and only shows it when writing the model back gives the same tokens; a query the model would change
/// is refused, never altered.
/// </summary>
public static class ReadBack
{
    public static ReadResult ReadAndVerify(string sql, string providerId, string? serverVersion) =>
        ReadAndVerify(sql, SqlReaders.For(providerId), SqlDialects.For(providerId, serverVersion));

    internal static ReadResult ReadAndVerify(string sql, ISqlReader reader, IDialect dialect)
    {
        var read = reader.Read(sql);
        if (read.Outcome == ReadOutcome.CannotShow || read.Statement is null)
        {
            return read;
        }

        var generated = SqlGenerator.Generate(read.Statement, dialect);
        if (generated.Sql is not { } written)
        {
            return CannotShow(read, generated.Problems
                .Select(p => new ReadReason("cannot-generate", $"The builder can't write this query back ({p.Code}): {p.Message}", 1))
                .ToArray());
        }

        var original = SqlTokens.Tokens(sql, dialect);
        var rewritten = SqlTokens.Tokens(written, dialect);
        var at = Enumerable.Range(0, Math.Max(original.Count, rewritten.Count))
            .FirstOrDefault(k => k >= original.Count || k >= rewritten.Count || original[k].Text != rewritten[k].Text, -1);
        if (at < 0)
        {
            return read;
        }

        var line = at < original.Count ? original[at].Line : original.LastOrDefault().Line;
        var message = $"Showing this query in the builder would change it: {Describe(original, at)} would become {Describe(rewritten, at)}.";
        return CannotShow(read, new ReadReason("would-change", message, Math.Max(line, 1)));
    }

    private static string Describe(List<(string Text, int Line, SqlTokenKind Kind)> tokens, int at) =>
        at < tokens.Count ? $"`{tokens[at].Text}`" : "the end of the query";

    private static ReadResult CannotShow(ReadResult read, params ReadReason[] reasons) =>
        new(ReadOutcome.CannotShow, null, reasons, 0, read.CommentCount);
}
