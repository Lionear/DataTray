namespace DataTray.Sdk.Query;

/// <summary>
/// One column of a result set. Beyond the display name and CLR type, the optional
/// <c>Base*</c>/<see cref="IsKey"/> metadata comes from the driver's column schema and
/// tells the host whether the column maps back to a real table column — the basis for
/// the editable-grid save-flow (see Notes.md §8). Computed/expression columns leave the
/// <c>Base*</c> fields null and are treated as read-only.
/// </summary>
public sealed record ResultColumn(string Name, Type ClrType)
{
    /// <summary>Schema of the underlying table, when the column maps to one.</summary>
    public string? BaseSchema { get; init; }

    /// <summary>Underlying table this column comes from; null for computed/joined expressions.</summary>
    public string? BaseTable { get; init; }

    /// <summary>Underlying column name (may differ from <see cref="Name"/> when aliased).</summary>
    public string? BaseColumn { get; init; }

    /// <summary>True when the column is part of the base table's primary key.</summary>
    public bool IsKey { get; init; }

    /// <summary>True when the column cannot be written (expression, generated, etc.).</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>True when the underlying column accepts NULL.</summary>
    public bool AllowDbNull { get; init; }
}

/// <summary>
/// A materialised result set. Rows are index-aligned with <see cref="Columns"/>.
/// The editable-grid work (change tracking, save-flow) builds on top of this;
/// see Notes.md §8.
/// </summary>
public sealed class QueryResult
{
    public required IReadOnlyList<ResultColumn> Columns { get; init; }
    public required IReadOnlyList<object?[]> Rows { get; init; }
    public int RecordsAffected { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// An opaque, provider-defined token for fetching the *next* page of a cursor-paged browse (see
    /// <see cref="Sdk.IDbProvider.ExecuteCursorPageAsync"/>). Non-null means "more rows may exist beyond
    /// this page — pass this token back to continue"; null means either the provider is not cursor-paging
    /// or the last page was reached. Only meaningful when <see cref="Sdk.IDbProvider.SupportsCursorPaging"/>.
    /// </summary>
    public string? NextCursor { get; init; }

    /// <summary>
    /// A trusted, provider-generated SQL boolean expression that must be ANDed into every generated
    /// UPDATE/DELETE WHERE clause for this result set, on top of the key-column predicate — e.g. a
    /// filtered unique index's filter (SQL Server: <c>sys.indexes.filter_definition</c>, "WHERE
    /// IsDeleted = 0") that makes a set of columns unique only for the rows it covers. Without it, a key
    /// built from those columns alone could match more than one row outside the filter (a duplicate
    /// soft-deleted row, say) and update/delete the wrong ones. Null for an ordinary primary key/unique
    /// constraint, which is already unique across the whole table. The provider is trusted to hand back
    /// only its own engine-generated text here — never anything derived from user input.
    /// </summary>
    public string? EditFilterPredicate { get; init; }
}
