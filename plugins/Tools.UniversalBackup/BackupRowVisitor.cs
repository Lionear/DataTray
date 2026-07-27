using DataTray.Sdk.Query;

namespace DataTray.Tools.UniversalBackup;

/// <summary>Bridges a provider's streamed rows (<see cref="IQueryRowVisitor"/>) into the streaming
/// <see cref="LbakWriter"/>: scalars go inline, LOB cells stream cell-by-cell so a multi-gigabyte value
/// is never materialised. Text vs binary LOB is decided from the result column's CLR type.</summary>
internal sealed class BackupRowVisitor(LbakWriter writer) : IQueryRowVisitor
{
    private IReadOnlyList<ResultColumn> _columns = [];

    public long RowCount { get; private set; }

    public Task OnColumnsAsync(IReadOnlyList<ResultColumn> columns, CancellationToken ct)
    {
        _columns = columns;
        return Task.CompletedTask;
    }

    public async Task OnRowAsync(IStreamedRow row, CancellationToken ct)
    {
        writer.BeginRow();
        for (var i = 0; i < row.FieldCount; i++)
        {
            if (row.IsNull(i))
            {
                writer.WriteScalarCell(null);
            }
            else if (!row.IsLob(i))
            {
                writer.WriteScalarCell(row.GetValue(i));
            }
            else if (i < _columns.Count && _columns[i].ClrType == typeof(string))
            {
                await writer.WriteLobTextCellAsync(row.GetTextReader(i), ct);
            }
            else
            {
                await writer.WriteLobBytesCellAsync(row.GetStream(i), ct);
            }
        }

        RowCount++;
    }
}
