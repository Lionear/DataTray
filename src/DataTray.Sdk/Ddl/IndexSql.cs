namespace DataTray.Sdk.Ddl;

/// <summary>
/// Builds the <c>CREATE INDEX</c> behind "New Index…". Shared because the statement is spelled the same
/// way by every engine the host ships — <c>CREATE [UNIQUE] INDEX name ON table (col [DESC], …)</c> — and
/// the one thing that differs, whether the table is qualified with a schema, is a parameter rather than
/// four copies of the same string. Providers still call it themselves: quoting is the dialect's, and a
/// provider whose syntax differs simply builds its own instead (the same freedom they have for CREATE
/// TABLE, which they all do build themselves, because that one genuinely differs).
/// </summary>
public static class IndexSql
{
    public static string Build(ISqlDialect dialect, CreateObjectSpec spec, bool qualifyWithSchema)
    {
        if (spec.Table is not { Length: > 0 } table)
        {
            throw new InvalidOperationException("An index needs the table it is created on.");
        }

        if (spec.IndexColumns.Count == 0)
        {
            throw new InvalidOperationException("An index needs at least one column.");
        }

        var qualified = qualifyWithSchema && spec.Schema is { Length: > 0 }
            ? $"{dialect.QuoteIdentifier(spec.Schema)}.{dialect.QuoteIdentifier(table)}"
            : dialect.QuoteIdentifier(table);

        // ASC is left implicit — it is the default everywhere, and a five-column index reads better when
        // only the descending ones are called out.
        var columns = spec.IndexColumns.Select(c =>
            c.Descending ? $"{dialect.QuoteIdentifier(c.Name)} DESC" : dialect.QuoteIdentifier(c.Name));

        var unique = spec.Unique ? "UNIQUE " : string.Empty;

        return $"CREATE {unique}INDEX {dialect.QuoteIdentifier(spec.Name)} ON {qualified} ({string.Join(", ", columns)})";
    }
}
