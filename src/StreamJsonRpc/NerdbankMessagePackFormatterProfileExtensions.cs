// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using Nerdbank.MessagePack;
using PolyType.Abstractions;
using static StreamJsonRpc.NerdbankMessagePackFormatter;

namespace StreamJsonRpc;

internal static class NerdbankMessagePackFormatterProfileExtensions
{
    internal static T? Deserialize<T>(this FormatterProfile profile, ref MessagePackReader reader, CancellationToken cancellationToken = default)
    {
        return profile.Serializer.Deserialize<T>(ref reader, profile.ShapeProvider, cancellationToken);
    }

    internal static T Deserialize<T>(this FormatterProfile profile, in ReadOnlySequence<byte> pack, CancellationToken cancellationToken = default)
    {
        // TODO: Improve the exception
        return profile.Serializer.Deserialize<T>(pack, profile.ShapeProvider, cancellationToken)
            ?? throw new MessagePackSerializationException(Resources.UnexpectedErrorProcessingJsonRpc);
    }

    internal static object? DeserializeObject(this FormatterProfile profile, in ReadOnlySequence<byte> pack, Type objectType, CancellationToken cancellationToken = default)
    {
        MessagePackReader reader = new(pack);
        return profile.Serializer.DeserializeObject(
            ref reader,
            profile.ShapeProvider.Resolve(objectType),
            cancellationToken);
    }

    internal static void Serialize<T>(this FormatterProfile profile, ref MessagePackWriter writer, T? value, CancellationToken cancellationToken = default)
    {
        profile.Serializer.Serialize(ref writer, value, profile.ShapeProvider, cancellationToken);
    }

    internal static void SerializeObject(this FormatterProfile profile, ref MessagePackWriter writer, object? value, Type objectType, CancellationToken cancellationToken = default)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        profile.Serializer.SerializeObject(ref writer, value, profile.ShapeProvider.Resolve(objectType), cancellationToken);
    }

    internal static void SerializeObject(this FormatterProfile profile, ref MessagePackWriter writer, object? value, CancellationToken cancellationToken = default)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        profile.Serializer.SerializeObject(ref writer, value, profile.ShapeProvider.Resolve(value.GetType()), cancellationToken);
    }
}
