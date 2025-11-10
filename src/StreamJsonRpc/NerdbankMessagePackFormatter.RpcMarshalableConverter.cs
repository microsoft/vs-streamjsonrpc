// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Nerdbank.MessagePack;
using PolyType;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    private class RpcMarshalableConverter<T>(
        ITypeShape<T> shape,
        JsonRpcProxyOptions proxyOptions,
        JsonRpcTargetOptions targetOptions,
        RpcMarshalableAttribute rpcMarshalableAttribute) : MessagePackConverter<T>
    ////where T : class // We expect this, but requiring it adds a constraint that some callers cannot statically satisfy.
    {
        [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "Reader is passed to rpc context")]
        public override T? Read(ref MessagePackReader reader, SerializationContext context)
        {
            NerdbankMessagePackFormatter formatter = context.GetFormatter();

            context.DepthStep();

#pragma warning disable NBMsgPack030 // Converters should not call top-level `MessagePackSerializer` methods - We need to switch from user data to envelope serializer
            MessageFormatterRpcMarshaledContextTracker.MarshalToken? token = formatter
                .envelopeSerializer.Deserialize(ref reader, PolyType.SourceGenerator.TypeShapeProvider_StreamJsonRpc.Default.Nullable_MarshalToken, context.CancellationToken);
#pragma warning restore NBMsgPack030 // Converters should not call top-level `MessagePackSerializer` methods

            return token.HasValue
                ? (T?)formatter.RpcMarshaledContextTracker.GetObject(typeof(T), token, proxyOptions, shape)
                : default;
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
                RpcTargetMetadata targetMetadata = RpcTargetMetadata.FromShape(shape);
                MessageFormatterRpcMarshaledContextTracker.MarshalToken token = formatter.RpcMarshaledContextTracker.GetToken(value, targetOptions, targetMetadata, rpcMarshalableAttribute);
                context.GetConverter<MessageFormatterRpcMarshaledContextTracker.MarshalToken>(Witness.GeneratedTypeShapeProvider).Write(ref writer, token, context);
            }
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => null;
    }
}
