// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

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
    private class EnumeratorResultsConverter<T> : MessagePackConverter<MessageFormatterEnumerableTracker.EnumeratorResults<T>>
    {
        [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "Reader is passed to user data context")]
        public override MessageFormatterEnumerableTracker.EnumeratorResults<T>? Read(ref MessagePackReader reader, SerializationContext context)
        {
            if (reader.TryReadNil())
            {
                return default;
            }

            NerdbankMessagePackFormatter formatter = context.GetFormatter();
            context.DepthStep();

            Verify.Operation(reader.ReadArrayHeader() == 2, "Expected array of length 2.");
            return new MessageFormatterEnumerableTracker.EnumeratorResults<T>()
            {
                Values = formatter.userDataProfile.Deserialize<IReadOnlyList<T>>(ref reader, context.CancellationToken),
                Finished = formatter.userDataProfile.Deserialize<bool>(ref reader, context.CancellationToken),
            };
        }

        [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "Writer is passed to user data context")]
        public override void Write(ref MessagePackWriter writer, in MessageFormatterEnumerableTracker.EnumeratorResults<T>? value, SerializationContext context)
        {
            if (value is null)
            {
                writer.WriteNil();
            }
            else
            {
                NerdbankMessagePackFormatter formatter = context.GetFormatter();
                context.DepthStep();

                writer.WriteArrayHeader(2);
                formatter.userDataProfile.Serialize(ref writer, value.Values, context.CancellationToken);
                formatter.userDataProfile.Serialize(ref writer, value.Finished, context.CancellationToken);
            }
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(EnumeratorResultsConverter<T>));
        }
    }
}
