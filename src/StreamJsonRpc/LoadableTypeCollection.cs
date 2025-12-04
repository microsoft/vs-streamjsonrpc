// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace StreamJsonRpc;

/// <summary>
/// A collection of instantiable types.
/// </summary>
/// <remarks>
/// This collection is persistent (immutable) and thread-safe.
/// </remarks>
public struct LoadableTypeCollection : IReadOnlyCollection<Type>
{
    private ImmutableDictionary<string, LoadableType>? collection;

    /// <inheritdoc />
    public int Count => this.collection?.Count ?? 0;

    private ImmutableDictionary<string, LoadableType> Collection => this.collection ?? ImmutableDictionary<string, LoadableType>.Empty;

    /// <summary>
    /// Adds a type to the collection.
    /// </summary>
    /// <param name="type">The type to be added.</param>
    public LoadableTypeCollection Add([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type type)
    {
        Requires.NotNull(type);
        Requires.Argument(type.FullName is not null, nameof(type), "FullName is null.");

        this.collection = this.Collection.SetItem(type.FullName, new LoadableType(type));
        return this;
    }

    /// <summary>Gets an enumerator over the <see cref="Type"/> elements of the collection.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<Type> GetEnumerator() => this.Collection.Select(t => t.Value.Type).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Type>)this).GetEnumerator();

    /// <summary>
    /// Retrieves a loadable type with the given <see cref="Type.FullName"/>, if there is one.
    /// </summary>
    /// <param name="fullName">The full name of the type to retrieve.</param>
    /// <param name="type">The retrieved type, if found.</param>
    /// <returns><see langword="true"/> if the type was found; otherwise, <see langword="false"/>.</returns>
    internal bool TryGetType(string fullName, [NotNullWhen(true), DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] out Type? type)
    {
        if (this.collection?.TryGetValue(fullName, out LoadableType loadableType) is true)
        {
            type = loadableType.Type;
            return true;
        }

        type = null;
        return false;
    }

    private struct LoadableType : IEquatable<LoadableType>
    {
        public LoadableType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type type)
        {
            this.Type = type;
        }

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
        public Type Type { get; }

        public bool Equals(LoadableType other) => this.Type == other.Type;

        public override int GetHashCode() => this.Type.GetHashCode();

        public override bool Equals(object? obj) => obj is LoadableType other && this.Equals(other);
    }
}
