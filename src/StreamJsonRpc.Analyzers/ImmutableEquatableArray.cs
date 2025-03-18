﻿using System.Collections;
using System.Collections.Immutable;

namespace StreamJsonRpc.Analyzers;

internal struct ImmutableEquatableArray<T>(ImmutableArray<T> inner) : IEquatable<ImmutableEquatableArray<T>>, IEnumerable<T>
{
    public int Length => inner.Length;

    private ImmutableArray<T> Inner => inner;

    public T this[int index] => inner[index];

    public ReadOnlySpan<T> AsSpan(int start, int length) => inner.AsSpan(start, length);

    public ReadOnlyMemory<T> AsMemory() => inner.AsMemory();

    public bool Equals(ImmutableEquatableArray<T> other) => inner.SequenceEqual(other.Inner);

    public override bool Equals(object obj) => obj is ImmutableEquatableArray<T> other && this.Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (T item in inner)
        {
            hash = (hash * 31) + item?.GetHashCode() ?? 0;
        }

        return hash;
    }

    public ImmutableArray<T>.Enumerator GetEnumerator() => inner.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator()
    {
        IEnumerable<T> enumerable = this.Inner;
        return enumerable.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        IEnumerable<T> enumerable = this.Inner;
        return enumerable.GetEnumerator();
    }
}
