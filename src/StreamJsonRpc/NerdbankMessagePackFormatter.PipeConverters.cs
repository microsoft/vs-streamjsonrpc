// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Text.Json.Nodes;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    private static class PipeConverters
    {
        internal class DuplexPipeConverter : MessagePackConverter<IDuplexPipe>
        {
            public static readonly DuplexPipeConverter DefaultInstance = new();

            public override IDuplexPipe? Read(ref MessagePackReader reader, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();

                if (reader.TryReadNil())
                {
                    return null;
                }

                return formatter.DuplexPipeTracker.GetPipe(reader.ReadUInt64());
            }

            public override void Write(ref MessagePackWriter writer, in IDuplexPipe? value, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();

                if (formatter.DuplexPipeTracker.GetULongToken(value) is ulong token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;
        }

        internal class PipeReaderConverter : MessagePackConverter<PipeReader>
        {
            public static readonly PipeReaderConverter DefaultInstance = new();

            public override PipeReader? Read(ref MessagePackReader reader, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();
                if (reader.TryReadNil())
                {
                    return null;
                }

                return formatter.DuplexPipeTracker.GetPipeReader(reader.ReadUInt64());
            }

            public override void Write(ref MessagePackWriter writer, in PipeReader? value, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();
                if (formatter.DuplexPipeTracker.GetULongToken(value) is { } token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;
        }

        internal class PipeWriterConverter : MessagePackConverter<PipeWriter>
        {
            public static readonly PipeWriterConverter DefaultInstance = new();

            public override PipeWriter? Read(ref MessagePackReader reader, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();
                if (reader.TryReadNil())
                {
                    return null;
                }

                return formatter.DuplexPipeTracker.GetPipeWriter(reader.ReadUInt64());
            }

            public override void Write(ref MessagePackWriter writer, in PipeWriter? value, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();
                if (formatter.DuplexPipeTracker.GetULongToken(value) is ulong token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;
        }

        internal class StreamConverter : MessagePackConverter<Stream>
        {
            public static readonly StreamConverter DefaultInstance = new();

            public override Stream? Read(ref MessagePackReader reader, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();
                if (reader.TryReadNil())
                {
                    return null;
                }

                return formatter.DuplexPipeTracker.GetPipe(reader.ReadUInt64()).AsStream();
            }

            public override void Write(ref MessagePackWriter writer, in Stream? value, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();
                if (formatter.DuplexPipeTracker.GetULongToken(value?.UsePipe()) is { } token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;
        }
    }
}
