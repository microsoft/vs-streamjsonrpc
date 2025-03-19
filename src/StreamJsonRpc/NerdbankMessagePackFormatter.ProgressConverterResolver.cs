// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Nerdbank.MessagePack;
using PolyType.Abstractions;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    private static class ProgressConverterResolver
    {
        public static MessagePackConverter<T> GetConverter<T>()
        {
            MessagePackConverter<T>? converter = default;

            if (MessageFormatterProgressTracker.CanDeserialize(typeof(T)))
            {
                converter = new FullProgressConverter<T>();
            }
            else if (MessageFormatterProgressTracker.CanSerialize(typeof(T)))
            {
                converter = new ProgressClientConverter<T>();
            }

            // TODO: Improve Exception
            return converter ?? throw new NotSupportedException();
        }

        /// <summary>
        /// Converts an instance of <see cref="IProgress{T}"/> to a progress token.
        /// </summary>
        private class ProgressClientConverter<TClass> : MessagePackConverter<TClass>
        {
            public override TClass Read(ref MessagePackReader reader, SerializationContext context)
            {
                throw new NotSupportedException("This formatter only serializes IProgress<T> instances.");
            }

            public override void Write(ref MessagePackWriter writer, in TClass? value, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();

                if (value is null)
                {
                    writer.WriteNil();
                }
                else
                {
                    long progressId = formatter.FormatterProgressTracker.GetTokenForProgress(value);
                    writer.Write(progressId);
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(ProgressClientConverter<TClass>));
            }
        }

        /// <summary>
        /// Converts a progress token to an <see cref="IProgress{T}"/> or an <see cref="IProgress{T}"/> into a token.
        /// </summary>
        private class FullProgressConverter<TClass> : MessagePackConverter<TClass>
        {
            [return: MaybeNull]
            public override TClass? Read(ref MessagePackReader reader, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();

                if (reader.TryReadNil())
                {
                    return default!;
                }

                Assumes.NotNull(formatter.JsonRpc);
                ReadOnlySequence<byte> token = reader.ReadRaw(context);
                bool clientRequiresNamedArgs = formatter.ApplicableMethodAttributeOnDeserializingMethod?.ClientRequiresNamedArguments is true;
                return (TClass)formatter.FormatterProgressTracker.CreateProgress(formatter.JsonRpc, token, typeof(TClass), clientRequiresNamedArgs);
            }

            public override void Write(ref MessagePackWriter writer, in TClass? value, SerializationContext context)
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();

                context.DepthStep();

                if (value is null)
                {
                    writer.WriteNil();
                }
                else
                {
                    long progressId = formatter.FormatterProgressTracker.GetTokenForProgress(value);
                    writer.Write(progressId);
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(FullProgressConverter<TClass>));
            }
        }
    }
}
