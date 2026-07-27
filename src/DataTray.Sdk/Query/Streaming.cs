namespace DataTray.Sdk.Query;

/// <summary>
/// Receives a streamed result set row-by-row (see <see cref="IDbProvider.StreamQueryAsync"/>).
/// Unlike <see cref="QueryResult"/> the rows are never all held in memory at once, and large
/// LOB cells are exposed as forward-only streams — so a table with a multi-gigabyte value can be
/// backed up without materialising it (the bug that motivated <c>.lbak</c> v2).
/// </summary>
public interface IQueryRowVisitor
{
    /// <summary>Called exactly once, before any row.</summary>
    Task OnColumnsAsync(IReadOnlyList<ResultColumn> columns, CancellationToken ct);

    /// <summary>Called once per row. The <paramref name="row"/> is only valid for the duration of
    /// the call — its streams must be consumed before returning (the underlying reader advances).</summary>
    Task OnRowAsync(IStreamedRow row, CancellationToken ct);
}

/// <summary>One row handed to an <see cref="IQueryRowVisitor"/>. Scalars are materialised via
/// <see cref="GetValue"/>; LOB cells (<see cref="IsLob"/>) are streamed via <see cref="GetStream"/>
/// or <see cref="GetTextReader"/> so they are never fully buffered.</summary>
public interface IStreamedRow
{
    int FieldCount { get; }
    bool IsNull(int i);

    /// <summary>True when column <paramref name="i"/> is an unbounded LOB (e.g. <c>varbinary(max)</c>,
    /// <c>nvarchar(max)</c>) and must be read through <see cref="GetStream"/>/<see cref="GetTextReader"/>
    /// rather than <see cref="GetValue"/>.</summary>
    bool IsLob(int i);

    /// <summary>Materialise a small/scalar value. Must not be called for a LOB cell.</summary>
    object? GetValue(int i);

    /// <summary>Forward-only stream over a binary LOB cell.</summary>
    Stream GetStream(int i);

    /// <summary>Forward-only reader over a text LOB cell.</summary>
    TextReader GetTextReader(int i);
}

/// <summary>A parameter for <see cref="IDbProvider.InsertStreamingAsync"/>: a placeholder name plus a
/// value that may itself be a stream (so a huge cell can be written straight from disk into the DB).</summary>
public sealed record StreamingParam(string Name, StreamingValue Value);

/// <summary>A streaming-insert value: either null, a materialised scalar, a binary stream or a text
/// reader. Streams are consumed once, in order, by the provider.</summary>
public sealed class StreamingValue
{
    public enum ValueKind { Null, Scalar, ByteStream, TextStream }

    private StreamingValue(ValueKind kind, object? scalar, Stream? byteStream, TextReader? textReader)
    {
        Kind = kind;
        Scalar = scalar;
        ByteStream = byteStream;
        TextReader = textReader;
    }

    public ValueKind Kind { get; }
    public object? Scalar { get; }
    public Stream? ByteStream { get; }
    public TextReader? TextReader { get; }

    public static StreamingValue Null { get; } = new(ValueKind.Null, null, null, null);
    public static StreamingValue Of(object? value) =>
        value is null ? Null : new(ValueKind.Scalar, value, null, null);
    public static StreamingValue Bytes(Stream stream) => new(ValueKind.ByteStream, null, stream, null);
    public static StreamingValue Text(TextReader reader) => new(ValueKind.TextStream, null, null, reader);
}

/// <summary>Adapts an already-materialised <c>object?[]</c> row to <see cref="IStreamedRow"/> — the
/// backing for the default (non-streaming) <see cref="IDbProvider.StreamQueryAsync"/> implementation
/// so providers that don't override it still satisfy the visitor contract.</summary>
public sealed class MaterializedStreamedRow(object?[] values) : IStreamedRow
{
    public int FieldCount => values.Length;
    public bool IsNull(int i) => values[i] is null;
    public bool IsLob(int i) => false;
    public object? GetValue(int i) => values[i];
    public Stream GetStream(int i) => new MemoryStream((byte[])values[i]!, writable: false);
    public TextReader GetTextReader(int i) => new StringReader((string)values[i]!);
}
