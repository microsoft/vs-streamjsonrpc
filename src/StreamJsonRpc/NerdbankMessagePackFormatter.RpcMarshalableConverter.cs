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
    private class RpcMarshalableConverter<T>(
        JsonRpcProxyOptions proxyOptions,
        JsonRpcTargetOptions targetOptions,
        RpcMarshalableAttribute rpcMarshalableAttribute) : MessagePackConverter<T>
        where T : class
    {
        [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "Reader is passed to rpc context")]
        public override T? Read(ref MessagePackReader reader, SerializationContext context)
        {
            NerdbankMessagePackFormatter formatter = context.GetFormatter();

            context.DepthStep();

            MessageFormatterRpcMarshaledContextTracker.MarshalToken? token = formatter
                .rpcProfile.Deserialize<MessageFormatterRpcMarshaledContextTracker.MarshalToken?>(ref reader, context.CancellationToken);

            return token.HasValue
                ? (T?)formatter.RpcMarshaledContextTracker.GetObject(typeof(T), token, proxyOptions)
                : null;
        }

        [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "Writer is passed to rpc context")]
        public override void Write(ref MessagePackWriter writer, in T? value, SerializationContext context)
        {
            NerdbankMessagePackFormatter formatter = context.GetFormatter();

            context.DepthStep();

            if (value is null)
            {
                writer.WriteNil();
            }
            else
            {
                MessageFormatterRpcMarshaledContextTracker.MarshalToken token = formatter.RpcMarshaledContextTracker.GetToken(value, targetOptions, typeof(T), rpcMarshalableAttribute);
                formatter.rpcProfile.Serialize(ref writer, token, context.CancellationToken);
            }
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(RpcMarshalableConverter<T>));
        }
    }
}
