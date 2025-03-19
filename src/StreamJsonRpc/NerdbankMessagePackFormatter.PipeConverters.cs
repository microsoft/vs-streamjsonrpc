// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Text.Json.Nodes;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType.Abstractions;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    private static class PipeConverters
    {
        internal class DuplexPipeConverter<T> : MessagePackConverter<T>
            where T : class, IDuplexPipe
        {
            public static readonly DuplexPipeConverter<T> DefaultInstance = new();

            public override T? Read(ref MessagePackReader reader, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();

                if (reader.TryReadNil())
                {
                    return null;
                }

                return (T)formatter.DuplexPipeTracker.GetPipe(reader.ReadUInt64());
            }

            public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
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

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(DuplexPipeConverter<T>));
            }
        }

        internal class PipeReaderConverter<T> : MessagePackConverter<T>
            where T : PipeReader
        {
            public static readonly PipeReaderConverter<T> DefaultInstance = new();

            public override T? Read(ref MessagePackReader reader, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();
                if (reader.TryReadNil())
                {
                    return null;
                }

                return (T)formatter.DuplexPipeTracker.GetPipeReader(reader.ReadUInt64());
            }

            public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
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

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(PipeReaderConverter<T>));
            }
        }

        internal class PipeWriterConverter<T> : MessagePackConverter<T>
            where T : PipeWriter
        {
            public static readonly PipeWriterConverter<T> DefaultInstance = new();

            public override T? Read(ref MessagePackReader reader, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();
                if (reader.TryReadNil())
                {
                    return null;
                }

                return (T)formatter.DuplexPipeTracker.GetPipeWriter(reader.ReadUInt64());
            }

            public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
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

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(PipeWriterConverter<T>));
            }
        }

        internal class StreamConverter<T> : MessagePackConverter<T>
            where T : Stream
        {
            public static readonly StreamConverter<T> DefaultInstance = new();

            public override T? Read(ref MessagePackReader reader, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();
                if (reader.TryReadNil())
                {
                    return null;
                }

                return (T)formatter.DuplexPipeTracker.GetPipe(reader.ReadUInt64()).AsStream();
            }

            public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
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

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(StreamConverter<T>));
            }
        }
    }
}
