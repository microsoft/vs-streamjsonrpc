// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;
using PolyType;
using PolyType.Abstractions;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
/// <remarks>
/// The MessagePack implementation used here comes from https://github.com/AArnott/Nerdbank.MessagePack.
/// </remarks>
public sealed partial class NerdbankMessagePackFormatter
{
    internal class FormatterContext(MessagePackSerializer serializer, ITypeShapeProvider shapeProvider)
    {
        public MessagePackSerializer Serializer => serializer;

        public ITypeShapeProvider ShapeProvider => shapeProvider;

        public T? Deserialize<T>(ref MessagePackReader reader, CancellationToken cancellationToken = default)
        {
            return serializer.Deserialize<T>(ref reader, shapeProvider, cancellationToken);
        }

        public T Deserialize<T>(in RawMessagePack pack, CancellationToken cancellationToken = default)
        {
            // TODO: Improve the exception
            return serializer.Deserialize<T>(pack, shapeProvider, cancellationToken)
                ?? throw new InvalidOperationException("Deserialization failed.");
        }

        public object? DeserializeObject(in RawMessagePack pack, Type objectType, CancellationToken cancellationToken = default)
        {
            MessagePackReader reader = new(pack);
            return serializer.DeserializeObject(
                ref reader,
                shapeProvider.Resolve(objectType),
                cancellationToken);
        }

        public void Serialize<T>(ref MessagePackWriter writer, T? value, CancellationToken cancellationToken = default)
        {
            serializer.Serialize(ref writer, value, shapeProvider, cancellationToken);
        }

        internal void SerializeObject(ref MessagePackWriter writer, object? value, Type objectType, CancellationToken cancellationToken = default)
        {
            serializer.SerializeObject(ref writer, value, shapeProvider.Resolve(objectType), cancellationToken);
        }
    }
}
