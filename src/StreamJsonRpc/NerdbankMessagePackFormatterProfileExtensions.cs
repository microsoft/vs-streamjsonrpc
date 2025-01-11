// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using Nerdbank.MessagePack;
using static StreamJsonRpc.NerdbankMessagePackFormatter;

namespace StreamJsonRpc;

/// <summary>
/// Extension methods for <see cref="Profile"/> that are specific to the <see cref="NerdbankMessagePackFormatter"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0016:Add public types and members to the declared API", Justification = "TODO: Temporary for development")]
public static class NerdbankMessagePackFormatterProfileExtensions
{
    /// <summary>
    /// Serializes an object using the specified <see cref="Profile"/>.
    /// </summary>
    /// <param name="profile">The formatter profile to use for serialization.</param>
    /// <param name="writer">The writer to which the object will be serialized.</param>
    /// <param name="value">The object to serialize.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    public static void SerializeObject(this Profile profile, ref MessagePackWriter writer, object? value, CancellationToken cancellationToken = default)
    {
        Requires.NotNull(profile, nameof(profile));
        SerializeObject(profile, ref writer, value, value?.GetType() ?? typeof(object), cancellationToken);
    }

    /// <summary>
    /// Serializes an object using the specified <see cref="Profile"/>.
    /// </summary>
    /// <param name="profile">The formatter profile to use for serialization.</param>
    /// <param name="writer">The writer to which the object will be serialized.</param>
    /// <param name="value">The object to serialize.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    public static void Serialize<T>(this Profile profile, ref MessagePackWriter writer, T? value, CancellationToken cancellationToken = default)
    {
        Requires.NotNull(profile, nameof(profile));

        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        PolyType.Abstractions.ITypeShape<T> shape = profile.ShapeProviderResolver.ResolveShape<T>();
        profile.Serializer.Serialize<T>(
            ref writer,
            value,
            shape,
            cancellationToken);
    }

    /// <summary>
    /// Deserializes a sequence of bytes into an object of type <typeparamref name="T"/> using the specified <see cref="Profile"/>.
    /// </summary>
    /// <typeparam name="T">The type of the object to deserialize.</typeparam>
    /// <param name="profile">The formatter profile to use for deserialization.</param>
    /// <param name="pack">The sequence of bytes to deserialize.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The deserialized object of type <typeparamref name="T"/>.</returns>
    /// <exception cref="MessagePackSerializationException">Thrown when deserialization fails.</exception>
    public static T? Deserialize<T>(this Profile profile, in ReadOnlySequence<byte> pack, CancellationToken cancellationToken = default)
    {
        Requires.NotNull(profile, nameof(profile));
        MessagePackReader reader = new(pack);
        return Deserialize<T>(profile, ref reader, cancellationToken);
    }

    internal static T? Deserialize<T>(this Profile profile, ref MessagePackReader reader, CancellationToken cancellationToken = default)
    {
        PolyType.ITypeShapeProvider provider = profile.ShapeProviderResolver.ResolveShapeProvider<T>();
        return profile.Serializer.Deserialize<T>(
            ref reader,
            provider,
            cancellationToken);
    }

    internal static object? DeserializeObject(this Profile profile, in ReadOnlySequence<byte> pack, Type objectType, CancellationToken cancellationToken = default)
    {
        MessagePackReader reader = new(pack);
        return DeserializeObject(profile, ref reader, objectType, cancellationToken);
    }

    internal static object? DeserializeObject(this Profile profile, ref MessagePackReader reader, Type objectType, CancellationToken cancellationToken = default)
    {
        PolyType.Abstractions.ITypeShape shape = profile.ShapeProviderResolver.ResolveShape(objectType);
        return profile.Serializer.DeserializeObject(
            ref reader,
            shape,
            cancellationToken);
    }

    internal static void SerializeObject(this Profile profile, ref MessagePackWriter writer, object? value, Type objectType, CancellationToken cancellationToken = default)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        PolyType.Abstractions.ITypeShape shape = profile.ShapeProviderResolver.ResolveShape(objectType);
        profile.Serializer.SerializeObject(
            ref writer,
            value,
            shape,
            cancellationToken);
    }
}
