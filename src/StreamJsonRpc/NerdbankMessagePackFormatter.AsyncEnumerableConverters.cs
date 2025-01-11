// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType.Abstractions;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    private static class AsyncEnumerableConverters
    {
        /// <summary>
        /// Converts an enumeration token to an <see cref="IAsyncEnumerable{T}"/>
        /// or an <see cref="IAsyncEnumerable{T}"/> into an enumeration token.
        /// </summary>
#pragma warning disable CA1812
        internal class PreciseTypeConverter<T> : MessagePackConverter<IAsyncEnumerable<T>>
#pragma warning restore CA1812
        {
            /// <summary>
            /// The constant "token", in its various forms.
            /// </summary>
            private static readonly MessagePackString TokenPropertyName = new(MessageFormatterEnumerableTracker.TokenPropertyName);

            /// <summary>
            /// The constant "values", in its various forms.
            /// </summary>
            private static readonly MessagePackString ValuesPropertyName = new(MessageFormatterEnumerableTracker.ValuesPropertyName);

            public override IAsyncEnumerable<T>? Read(ref MessagePackReader reader, SerializationContext context)
            {
                if (reader.TryReadNil())
                {
                    return default;
                }

                NerdbankMessagePackFormatter mainFormatter = context.GetFormatter();

                context.DepthStep();

                RawMessagePack? token = default;
                IReadOnlyList<T>? initialElements = null;
                int propertyCount = reader.ReadMapHeader();
                for (int i = 0; i < propertyCount; i++)
                {
                    if (TokenPropertyName.TryRead(ref reader))
                    {
                        // The value needs to outlive the reader, so we clone it.
                        token = new RawMessagePack(reader.ReadRaw(context)).ToOwned();
                    }
                    else if (ValuesPropertyName.TryRead(ref reader))
                    {
                        initialElements = context.GetConverter<IReadOnlyList<T>>(context.TypeShapeProvider).Read(ref reader, context);
                    }
                    else
                    {
                        reader.Skip(context);
                    }
                }

                return mainFormatter.EnumerableTracker.CreateEnumerableProxy(token.HasValue ? token.Value : null, initialElements);
            }

            [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "Writer is passed to helper method")]
            public override void Write(ref MessagePackWriter writer, in IAsyncEnumerable<T>? value, SerializationContext context)
            {
                context.DepthStep();

                NerdbankMessagePackFormatter mainFormatter = context.GetFormatter();
                Serialize_Shared(mainFormatter, ref writer, value, context);
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(PreciseTypeConverter<T>));
            }

            internal static void Serialize_Shared(NerdbankMessagePackFormatter mainFormatter, ref MessagePackWriter writer, IAsyncEnumerable<T>? value, SerializationContext context)
            {
                if (value is null)
                {
                    writer.WriteNil();
                }
                else
                {
                    (IReadOnlyList<T> elements, bool finished) = value.TearOffPrefetchedElements();
                    long token = mainFormatter.EnumerableTracker.GetToken(value);

                    int propertyCount = 0;
                    if (elements.Count > 0)
                    {
                        propertyCount++;
                    }

                    if (!finished)
                    {
                        propertyCount++;
                    }

                    writer.WriteMapHeader(propertyCount);

                    if (!finished)
                    {
                        writer.Write(TokenPropertyName);
                        writer.Write(token);
                    }

                    if (elements.Count > 0)
                    {
                        writer.Write(ValuesPropertyName);
                        context.GetConverter<IReadOnlyList<T>>(context.TypeShapeProvider).Write(ref writer, elements, context);
                    }
                }
            }
        }

        /// <summary>
        /// Converts an instance of <see cref="IAsyncEnumerable{T}"/> to an enumeration token.
        /// </summary>
#pragma warning disable CA1812
        internal class GeneratorConverter<TClass, TElement> : MessagePackConverter<TClass>
            where TClass : IAsyncEnumerable<TElement>
#pragma warning restore CA1812
        {
            public override TClass Read(ref MessagePackReader reader, SerializationContext context)
            {
                throw new NotSupportedException();
            }

            [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "Writer is passed to helper method")]
            public override void Write(ref MessagePackWriter writer, in TClass? value, SerializationContext context)
            {
                NerdbankMessagePackFormatter mainFormatter = context.GetFormatter();

                context.DepthStep();
                PreciseTypeConverter<TElement>.Serialize_Shared(mainFormatter, ref writer, value, context);
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(GeneratorConverter<TClass, TElement>));
            }
        }
    }
}
