// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using Nerdbank.MessagePack;
using PolyType;
using StreamJsonRpc.Protocol;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    internal class TraceParentAsBinaryConverter : MessagePackConverter<TraceParent>
    {
        internal static readonly TraceParentAsBinaryConverter Instance = new();

        public unsafe override TraceParent Read(ref MessagePackReader reader, SerializationContext context)
        {
            context.DepthStep();

            if (reader.ReadArrayHeader() != 2)
            {
                throw new NotSupportedException("Unexpected array length.");
            }

            var result = default(TraceParent);
            result.Version = reader.ReadByte();
            if (result.Version != 0)
            {
                throw new NotSupportedException("traceparent version " + result.Version + " is not supported.");
            }

            if (reader.ReadArrayHeader() != 3)
            {
                throw new NotSupportedException("Unexpected array length in version-format.");
            }

            ReadOnlySequence<byte> bytes = reader.ReadBytes() ?? throw new NotSupportedException("Expected traceid not found.");
            bytes.CopyTo(new Span<byte>(result.TraceId, TraceParent.TraceIdByteCount));

            bytes = reader.ReadBytes() ?? throw new NotSupportedException("Expected parentid not found.");
            bytes.CopyTo(new Span<byte>(result.ParentId, TraceParent.ParentIdByteCount));

            result.Flags = (TraceParent.TraceFlags)reader.ReadByte();

            return result;
        }

        public unsafe override void Write(ref MessagePackWriter writer, in TraceParent value, SerializationContext context)
        {
            if (value.Version != 0)
            {
                throw new NotSupportedException("traceparent version " + value.Version + " is not supported.");
            }

            context.DepthStep();

            writer.WriteArrayHeader(2);

            writer.Write(value.Version);

            writer.WriteArrayHeader(3);

            fixed (byte* traceId = value.TraceId)
            {
                writer.Write(new ReadOnlySpan<byte>(traceId, TraceParent.TraceIdByteCount));
            }

            fixed (byte* parentId = value.ParentId)
            {
                writer.Write(new ReadOnlySpan<byte>(parentId, TraceParent.ParentIdByteCount));
            }

            writer.Write((byte)value.Flags);
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;
    }

    internal class TraceParentAsStringConverter : MessagePackConverter<TraceParent>
    {
        internal static readonly TraceParentAsStringConverter Instance = new();

        public override TraceParent Read(ref MessagePackReader reader, SerializationContext context)
        {
            context.DepthStep();

            ReadOnlySequence<byte> utf8Sequence = reader.ReadStringSequence() ?? throw new MessagePackSerializationException("Unexpected null value.");
            if (utf8Sequence.Length != TraceParent.Length)
            {
                throw new MessagePackSerializationException("Unexpected length for traceparent string.");
            }

            Span<byte> utf8Bytes = stackalloc byte[TraceParent.Length];
            utf8Sequence.CopyTo(utf8Bytes);

            Span<char> chars = stackalloc char[TraceParent.Length];
            if (!Encoding.UTF8.TryGetChars(utf8Bytes, chars, out int charsWritten))
            {
                throw new MessagePackSerializationException("Invalid UTF-8 in traceparent string.");
            }

            return new TraceParent(chars);
        }

        public override void Write(ref MessagePackWriter writer, in TraceParent value, SerializationContext context)
        {
            if (value.Version != 0)
            {
                throw new NotSupportedException("traceparent version " + value.Version + " is not supported.");
            }

            context.DepthStep();

            Span<char> chars = stackalloc char[TraceParent.Length];
            value.WriteTo(chars);
            writer.Write(chars);
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;
    }
}
