using System.Collections;

namespace SimNetGateway.Engine;

/// <summary>Read-only, allocation-free view of a <see cref="List{T}"/> (indexer, Count and a struct enumerator).</summary>
public readonly struct ReadOnlyListView<T> : IReadOnlyList<T>
{
    private readonly List<T>? _list;

    internal ReadOnlyListView(List<T> list) => _list = list;

    public int Count => _list?.Count ?? 0;

    public T this[int index] => _list is null ? throw new ArgumentOutOfRangeException(nameof(index)) : _list[index];

    public Enumerator GetEnumerator() => new(_list);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)(_list ?? new List<T>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)(_list ?? new List<T>())).GetEnumerator();

    public struct Enumerator
    {
        private readonly List<T>? _list;
        private int _index;

        internal Enumerator(List<T>? list)
        {
            _list = list;
            _index = -1;
        }

        public bool MoveNext() => _list is not null && ++_index < _list.Count;

        public T Current => _list![_index];
    }
}
