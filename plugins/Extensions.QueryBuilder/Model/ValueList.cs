using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DataTray.Extensions.QueryBuilder.Model;

/// <summary>Immutable list with sequence equality, so records holding lists compare by value.</summary>
[JsonConverter(typeof(ValueListConverterFactory))]
[CollectionBuilder(typeof(ValueList), nameof(ValueList.Create))]
public sealed class ValueList<T>(IEnumerable<T> items) : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly T[] _items = items.ToArray();

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(ValueList<T>? other) => other is not null && _items.AsSpan().SequenceEqual(other._items);

    public override bool Equals(object? obj) => Equals(obj as ValueList<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    public static implicit operator ValueList<T>(T[] items) => new(items);
}
