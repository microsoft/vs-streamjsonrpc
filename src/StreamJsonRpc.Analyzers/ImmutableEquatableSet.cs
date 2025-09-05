// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Originally copied from https://github.com/eiriktsarpalis/PolyType/blob/f8b389f359df0404f4467d5bcc95fe0174268543/src/PolyType.Roslyn/IncrementalTypes/ImmutableEquatableSet.cs#L1
// Copyright (c) 2023 Eirik Tsarpalis
#pragma warning disable SA1402 // File may only contain a single type

using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// Defines extension methods for creating <see cref="ImmutableEquatableSet{T}"/> instances.
/// </summary>
public static class ImmutableEquatableSet
{
    /// <summary>
    /// Creates a new <see cref="ImmutableEquatableSet{T}"/> instance from the specified values.
    /// </summary>
    /// <typeparam name="T">The element type of the set.</typeparam>
    /// <param name="values">The source enumerable with which to populate the set.</param>
    /// <returns>A new <see cref="ImmutableEquatableSet{T}"/> instance containing the specified values.</returns>
    public static ImmutableEquatableSet<T> ToImmutableEquatableSet<T>(this IEnumerable<T> values)
        where T : IEquatable<T>
        => values is ICollection<T> { Count: 0 } ? ImmutableEquatableSet<T>.Empty : ImmutableEquatableSet<T>.UnsafeCreateFromHashSet([.. values]);

    /// <summary>
    /// Creates a new <see cref="ImmutableEquatableSet{T}"/> instance from the specified values.
    /// </summary>
    /// <typeparam name="T">The element type of the set.</typeparam>
    /// <param name="values">The source span with which to populate the set.</param>
    /// <returns>A new <see cref="ImmutableEquatableSet{T}"/> instance containing the specified values.</returns>
    public static ImmutableEquatableSet<T> Create<T>(ReadOnlySpan<T> values)
        where T : IEquatable<T>
    {
        if (values.IsEmpty)
        {
            return ImmutableEquatableSet<T>.Empty;
        }

        HashSet<T> hashSet = [];
        for (int i = 0; i < values.Length; i++)
        {
            hashSet.Add(values[i]);
        }

        return ImmutableEquatableSet<T>.UnsafeCreateFromHashSet(hashSet);
    }
}

/// <summary>
/// Defines an immutable set that defines structural equality semantics.
/// </summary>
/// <typeparam name="T">The element type of the set.</typeparam>
[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(ImmutableEquatableSet<>.DebugView))]
[CollectionBuilder(typeof(ImmutableEquatableSet), nameof(ImmutableEquatableSet.Create))]
public sealed class ImmutableEquatableSet<T> :
    IEquatable<ImmutableEquatableSet<T>>,
    ISet<T>,
    IReadOnlyCollection<T>,
    ICollection

    where T : IEquatable<T>
{
    private readonly HashSet<T> values;

    private ImmutableEquatableSet(HashSet<T> values)
    {
        Debug.Assert(values.Comparer == EqualityComparer<T>.Default, "Unexpected comparer.");
        this.values = values;
    }

    /// <summary>
    /// Gets an empty <see cref="ImmutableEquatableSet{T}"/> instance.
    /// </summary>
    public static ImmutableEquatableSet<T> Empty { get; } = new([]);

    /// <summary>
    /// Gets an enumerator that iterates through the set.
    /// </summary>
    public int Count => this.values.Count;

    bool ICollection<T>.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    /// <summary>
    /// Checks if the set contains the specified item.
    /// </summary>
    public bool Contains(T item)
        => this.values.Contains(item);

    /// <inheritdoc/>
    public bool Equals(ImmutableEquatableSet<T> other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        HashSet<T> thisSet = this.values;
        HashSet<T> otherSet = other.values;
        if (thisSet.Count != otherSet.Count)
        {
            return false;
        }

        foreach (T value in thisSet)
        {
            if (!otherSet.Contains(value))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj is ImmutableEquatableSet<T> other && this.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        int hash = 0;
        foreach (T value in this.values)
        {
            hash ^= value is null ? 0 : value.GetHashCode();
        }

        return hash;
    }

    /// <summary>
    /// Gets an enumerator that iterates through the set.
    /// </summary>
    public HashSet<T>.Enumerator GetEnumerator() => this.values.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => this.values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.values.GetEnumerator();

    void ICollection<T>.CopyTo(T[] array, int arrayIndex) => this.values.CopyTo(array, arrayIndex);

    void ICollection.CopyTo(Array array, int index) => throw new InvalidOperationException();

    bool ISet<T>.IsSubsetOf(IEnumerable<T> other) => this.values.IsSubsetOf(other);

    bool ISet<T>.IsSupersetOf(IEnumerable<T> other) => this.values.IsSupersetOf(other);

    bool ISet<T>.IsProperSubsetOf(IEnumerable<T> other) => this.values.IsProperSubsetOf(other);

    bool ISet<T>.IsProperSupersetOf(IEnumerable<T> other) => this.values.IsProperSupersetOf(other);

    bool ISet<T>.Overlaps(IEnumerable<T> other) => this.values.Overlaps(other);

    bool ISet<T>.SetEquals(IEnumerable<T> other) => this.values.SetEquals(other);

    void ICollection<T>.Add(T item) => throw new InvalidOperationException();

    bool ISet<T>.Add(T item) => throw new InvalidOperationException();

    void ISet<T>.UnionWith(IEnumerable<T> other) => throw new InvalidOperationException();

    void ISet<T>.IntersectWith(IEnumerable<T> other) => throw new InvalidOperationException();

    void ISet<T>.ExceptWith(IEnumerable<T> other) => throw new InvalidOperationException();

    void ISet<T>.SymmetricExceptWith(IEnumerable<T> other) => throw new InvalidOperationException();

    bool ICollection<T>.Remove(T item) => throw new InvalidOperationException();

    void ICollection<T>.Clear() => throw new InvalidOperationException();

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static ImmutableEquatableSet<T> UnsafeCreateFromHashSet(HashSet<T> values)
        => new(values);

    private sealed class DebugView(ImmutableEquatableSet<T> set)
    {
        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public T[] Items => set.ToArray();
    }
}
