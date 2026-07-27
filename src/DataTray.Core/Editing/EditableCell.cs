using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace DataTray.Core.Editing;

/// <summary>
/// One cell in an <see cref="EditableRow"/>. Editing binds two-way to <see cref="Value"/> — a
/// plain property, so the DataGrid commits edits reliably (a bare row-indexer binding does not).
/// <see cref="IsModified"/> drives the "changed until save/discard" cell highlight.
/// </summary>
public sealed class EditableCell : INotifyPropertyChanged
{
    private readonly EditableRow _row;
    private object? _value;

    internal EditableCell(EditableRow row, object? original, object? value)
    {
        _row = row;
        Original = original;
        _value = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public object? Original { get; private set; }

    public object? Value
    {
        get => _value;
        set
        {
            if (Equals(_value, value))
            {
                return;
            }

            _value = value;
            if (value is not null)
            {
                IsExplicitNull = false;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(EditText));
            OnPropertyChanged(nameof(BoolValue));
            OnPropertyChanged(nameof(DateValue));
            OnPropertyChanged(nameof(DateText));
            OnPropertyChanged(nameof(TimeText));
            OnPropertyChanged(nameof(IsModified));
            _row.NotifyCellEdited();
        }
    }

    /// <summary>
    /// True when this cell was deliberately set to NULL rather than never filled in. Only meaningful on
    /// an added row: an INSERT leaves unset columns to the database (defaults, auto-increment keys), but
    /// a column the user pointed at and set to NULL has to be written as an explicit NULL — otherwise a
    /// column with a DEFAULT silently gets the default instead of the NULL that was asked for.
    /// </summary>
    public bool IsExplicitNull { get; private set; }

    /// <summary>
    /// Two-way binding target for the grid's cell editor: the value as text, with one rule — empty text
    /// over a NULL cell leaves the NULL alone. Opening a NULL cell's editor and leaving it must not
    /// silently rewrite the NULL to an empty string, and the editor cannot tell "the user cleared this"
    /// from "the editor handed back what it was given". Writing an empty string into a NULL cell is
    /// therefore a deliberate action (<see cref="SetEmpty"/>), not something tabbing through can do.
    /// </summary>
    public string? EditText
    {
        get => _value is null ? string.Empty : Convert.ToString(_value, CultureInfo.InvariantCulture);
        set
        {
            if (string.IsNullOrEmpty(value) && _value is null)
            {
                return;
            }

            Value = value;
        }
    }

    /// <summary>
    /// Two-way binding target for a checkbox editor on a boolean column: the value as a nullable bool,
    /// where null is SQL NULL. Clearing a three-state checkbox therefore sets NULL deliberately rather
    /// than falling back to false — on a nullable column those are different rows.
    /// </summary>
    public bool? BoolValue
    {
        get => _value switch
        {
            null => null,
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            // Numeric booleans: SQLite has no bool type, MySQL's is tinyint(1).
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n != 0,
            IConvertible c => System.Convert.ToInt64(c, CultureInfo.InvariantCulture) != 0,
            _ => null
        };
        set
        {
            if (value is null)
            {
                SetNull();
                return;
            }

            Value = value.Value;
        }
    }

    /// <summary>
    /// Two-way binding target for a date-picker editor: the value as a nullable date, where null is SQL
    /// NULL. Picking a date keeps the cell's existing time of day — a picker only edits the date half,
    /// and a datetime column would otherwise silently lose its time.
    /// </summary>
    public DateTime? DateValue
    {
        get => _value switch
        {
            null => null,
            DateTime d => d,
            DateTimeOffset o => o.DateTime,
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) => parsed,
            _ => null
        };
        set
        {
            if (value is null)
            {
                SetNull();
                return;
            }

            var time = DateValue?.TimeOfDay ?? TimeSpan.Zero;
            Value = value.Value.Date + time;
        }
    }

    /// <summary>
    /// Two-way binding target for the text half of a date editor: the value in ISO form
    /// (<c>yyyy-MM-dd</c>, plus <c>HH:mm:ss</c> when the cell carries a time), which is unambiguous
    /// regardless of the machine's locale. Clearing the text is NULL — a date column has no empty
    /// string to fall back to. Text that doesn't parse is kept as typed, so the save reports it rather
    /// than the editor silently discarding what was entered.
    /// </summary>
    public string? DateText
    {
        get => DateValue is not { } date
            ? string.Empty
            : date.TimeOfDay == TimeSpan.Zero
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                SetNull();
                return;
            }

            Value = DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : value;
        }
    }

    /// <summary>
    /// Two-way binding target for the time half of a date editor (<c>HH:mm:ss</c>). A calendar only picks
    /// a date, so this is where the time of a timestamp is edited. Text that isn't a time yet is ignored
    /// rather than written, so typing through "1", "13", "13:" doesn't rewrite the cell three times.
    /// Setting a time on a NULL cell dates it today — otherwise the input would vanish with no feedback.
    /// </summary>
    public string? TimeText
    {
        get => DateValue is { } date ? date.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;
        set
        {
            if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time))
            {
                return;
            }

            Value = (DateValue?.Date ?? DateTime.Today) + time;
        }
    }

    /// <summary>Set this cell to NULL deliberately — see <see cref="IsExplicitNull"/>.</summary>
    public void SetNull()
    {
        // Set the flag first: assigning null to an already-null cell short-circuits in the setter, and an
        // added row's cells start out null, which is exactly the case the flag exists for.
        IsExplicitNull = true;
        Value = null;
    }

    /// <summary>Set this cell to an empty string — the counterpart to <see cref="SetNull"/>.</summary>
    public void SetEmpty() => Value = string.Empty;

    /// <summary>True when this cell of an existing row differs from its loaded value.</summary>
    public bool IsModified => _row.State != RowState.Added && !SameValue(_value, Original);

    // Re-baseline after a save (unused when the host re-queries, but keeps the model consistent).
    internal void AcceptChanges()
    {
        Original = _value;
        OnPropertyChanged(nameof(IsModified));
    }

    internal void RaiseModifiedChanged() => OnPropertyChanged(nameof(IsModified));

    // Compare by invariant string form so an edit that re-types the same value (e.g. "3" over 3L)
    // doesn't light up as changed — good enough for the highlight; the save-flow coerces properly.
    private static bool SameValue(object? a, object? b)
    {
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return string.Equals(
            Convert.ToString(a, CultureInfo.InvariantCulture),
            Convert.ToString(b, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
