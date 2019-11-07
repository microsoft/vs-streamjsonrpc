// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.Serialization;
    using MessagePack;
    using MessagePack.Formatters;
    using MessagePack.Resolvers;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;
    using Nerdbank.Streams;
    using StreamJsonRpc.Protocol;
    using StreamJsonRpc.Reflection;

    /// <summary>
    /// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
    /// </summary>
    /// <remarks>
    /// The MessagePack implementation used here comes from https://github.com/neuecc/MessagePack-CSharp.
    /// The README on that project site describes use cases and its performance compared to alternative
    /// .NET MessagePack implementations and this one appears to be the best by far.
    /// </remarks>
    public class MessagePackFormatter : IJsonRpcMessageFormatter, IJsonRpcInstanceContainer
    {
        private static readonly MessagePackSerializerOptions StandardOptions;

        /// <summary>
        /// The options to use for serialization.
        /// </summary>
        private readonly MessagePackSerializerOptions options;

        /// <summary>
        /// <see cref="MessageFormatterProgressTracker"/> instance containing useful methods to help on the implementation of message formatters.
        /// </summary>
        private readonly MessageFormatterProgressTracker formatterProgressTracker = new MessageFormatterProgressTracker();

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcInstanceContainer.Rpc"/> property.
        /// </summary>
        private JsonRpc? rpc;

        static MessagePackFormatter()
        {
            var formatters = new IMessagePackFormatter[]
            {
                RequestIdFormatter.Instance,
                JsonRpcMessageFormatter.Instance,
                JsonRpcRequestFormatter.Instance,
                JsonRpcResultFormatter.Instance,
                JsonRpcErrorDetailFormatter.Instance,
            };
            var resolvers = new IFormatterResolver[]
            {
                StandardResolverAllowPrivate.Instance,
            };
            var compositeResolver = CompositeResolver.Create(formatters, resolvers);

            StandardOptions = MessagePackSerializerOptions.Standard.WithResolver(compositeResolver);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MessagePackFormatter"/> class
        /// with LZ4 compression.
        /// </summary>
        public MessagePackFormatter()
            : this(compress: true)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MessagePackFormatter"/> class.
        /// </summary>
        /// <param name="compress">A value indicating whether to use LZ4 compression.</param>
        public MessagePackFormatter(bool compress)
        {
            this.options = StandardOptions.WithLZ4Compression(useLZ4Compression: compress);
        }

        /// <inheritdoc/>
        JsonRpc IJsonRpcInstanceContainer.Rpc
        {
            set
            {
                Verify.Operation(this.rpc == null, "This formatter already belongs to another JsonRpc instance. Create a new instance of this formatter for each new JsonRpc instance.");

                this.rpc = value;
            }
        }

        /// <inheritdoc/>
        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer) => MessagePackSerializer.Deserialize<JsonRpcMessage>(contentBuffer, this.options);

        /// <inheritdoc/>
        public void Serialize(IBufferWriter<byte> contentBuffer, JsonRpcMessage message)
        {
            if (message is Protocol.JsonRpcRequest request && request.Arguments != null && request.ArgumentsList == null && !(request.Arguments is IReadOnlyDictionary<string, object>))
            {
                // This request contains named arguments, but not using a standard dictionary. Convert it to a dictionary so that
                // the parameters can be matched to the method we're invoking.
                request.Arguments = GetParamsObjectDictionary(request.Arguments);
            }

            var writer = new MessagePackWriter(contentBuffer);
            MessagePackSerializer.Serialize(ref writer, message, this.options);
            writer.Flush();
        }

        /// <inheritdoc/>
        public object GetJsonText(JsonRpcMessage message) => MessagePackSerializer.SerializeToJson(message, this.options);

        /// <summary>
        /// Extracts a dictionary of property names and values from the specified params object.
        /// </summary>
        /// <param name="paramsObject">The params object.</param>
        /// <returns>A dictionary, or <c>null</c> if <paramref name="paramsObject"/> is null.</returns>
        /// <remarks>
        /// In its present implementation, this method disregards any renaming attributes that would give
        /// the properties on the parameter object a different name. The <see cref="MessagePackSerializer"/>
        /// doesn't expose a simple way of doing this, so we'd have to emulate it by supporting both
        /// <see cref="DataMemberAttribute.Name"/> and <see cref="KeyAttribute.StringKey"/> handling.
        /// </remarks>
        [return: NotNullIfNotNull("paramsObject")]
        private static Dictionary<string, object?>? GetParamsObjectDictionary(object? paramsObject)
        {
            if (paramsObject == null)
            {
                return null;
            }

            if (paramsObject is IReadOnlyDictionary<object, object?> dictionary)
            {
                // Anonymous types are serialized this way.
                return dictionary.ToDictionary(kv => (string)kv.Key, kv => kv.Value);
            }

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);

            const BindingFlags bindingFlags = BindingFlags.FlattenHierarchy | BindingFlags.Public | BindingFlags.Instance;
            foreach (var property in paramsObject.GetType().GetTypeInfo().GetProperties(bindingFlags))
            {
                if (property.GetMethod != null)
                {
                    result[property.Name] = property.GetValue(paramsObject);
                }
            }

            foreach (var field in paramsObject.GetType().GetTypeInfo().GetFields(bindingFlags))
            {
                result[field.Name] = field.GetValue(paramsObject);
            }

            return result;
        }

        private static ReadOnlySequence<byte> GetSliceForNextToken(ref MessagePackReader reader)
        {
            var startingPosition = reader.Position;
            reader.Skip();
            var endingPosition = reader.Position;
            return reader.Sequence.Slice(startingPosition, endingPosition);
        }

        private class RequestIdFormatter : IMessagePackFormatter<RequestId>
        {
            internal static readonly RequestIdFormatter Instance = new RequestIdFormatter();

            private RequestIdFormatter()
            {
            }

            public RequestId Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                if (reader.NextMessagePackType == MessagePackType.Integer)
                {
                    return new RequestId(reader.ReadInt64());
                }
                else
                {
                    return new RequestId(reader.ReadString());
                }
            }

            public void Serialize(ref MessagePackWriter writer, RequestId value, MessagePackSerializerOptions options)
            {
                if (value.Number.HasValue)
                {
                    writer.Write(value.Number.Value);
                }
                else
                {
                    writer.Write(value.String);
                }
            }
        }

        private class JsonRpcMessageFormatter : IMessagePackFormatter<JsonRpcMessage>
        {
            internal static readonly JsonRpcMessageFormatter Instance = new JsonRpcMessageFormatter();

            private JsonRpcMessageFormatter()
            {
            }

            private enum MessageSubTypes
            {
                Request,
                Result,
                Error,
            }

            public JsonRpcMessage Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                int count = reader.ReadArrayHeader();
                if (count != 2)
                {
                    throw new IOException("Unexpected msgpack sequence.");
                }

                switch ((MessageSubTypes)reader.ReadInt32())
                {
                    case MessageSubTypes.Request: return options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcRequest>().Deserialize(ref reader, options);
                    case MessageSubTypes.Result: return options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcResult>().Deserialize(ref reader, options);
                    case MessageSubTypes.Error: return options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError>().Deserialize(ref reader, options);
                    case MessageSubTypes value: throw new NotSupportedException("Unexpected distinguishing subtype value: " + value);
                }
            }

            public void Serialize(ref MessagePackWriter writer, JsonRpcMessage value, MessagePackSerializerOptions options)
            {
                Requires.NotNull(value, nameof(value));

                writer.WriteArrayHeader(2);
                switch (value)
                {
                    case Protocol.JsonRpcRequest request:
                        writer.Write((int)MessageSubTypes.Request);
                        options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcRequest>().Serialize(ref writer, request, options);
                        break;
                    case Protocol.JsonRpcResult result:
                        writer.Write((int)MessageSubTypes.Result);
                        options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcResult>().Serialize(ref writer, result, options);
                        break;
                    case Protocol.JsonRpcError error:
                        writer.Write((int)MessageSubTypes.Error);
                        options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError>().Serialize(ref writer, error, options);
                        break;
                    default:
                        throw new NotSupportedException("Unexpected JsonRpcMessage-derived type: " + value.GetType().Name);
                }
            }
        }

        private class JsonRpcRequestFormatter : IMessagePackFormatter<Protocol.JsonRpcRequest>
        {
            internal static readonly JsonRpcRequestFormatter Instance = new JsonRpcRequestFormatter();

            private JsonRpcRequestFormatter()
            {
            }

            public Protocol.JsonRpcRequest Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                int arrayLength = reader.ReadArrayHeader();
                if (arrayLength != 4)
                {
                    throw new IOException("Unexpected length of request.");
                }

                var result = new JsonRpcRequest
                {
                    Version = reader.ReadString(),
                    RequestId = options.Resolver.GetFormatterWithVerify<RequestId>().Deserialize(ref reader, options),
                    Method = reader.ReadString(),
                };

                // Parse out the arguments into a dictionary or array, but don't deserialize them because we don't yet know what types to deserialize them to.
                switch (reader.NextMessagePackType)
                {
                    case MessagePackType.Array:
                        var positionalArgs = new ReadOnlySequence<byte>[reader.ReadArrayHeader()];
                        for (int i = 0; i < positionalArgs.Length; i++)
                        {
                            positionalArgs[i] = GetSliceForNextToken(ref reader);
                        }

                        result.MsgPackPositionalArguments = positionalArgs;
                        break;
                    case MessagePackType.Map:
                        int namedArgsCount = reader.ReadMapHeader();
                        var namedArgs = new Dictionary<string, ReadOnlySequence<byte>>(namedArgsCount);
                        for (int i = 0; i < namedArgsCount; i++)
                        {
                            string propertyName = reader.ReadString();
                            namedArgs.Add(propertyName, GetSliceForNextToken(ref reader));
                        }

                        result.MsgPackNamedArguments = namedArgs;
                        break;
                    case MessagePackType type:
                        throw new MessagePackSerializationException("Expected a map or array of arguments but got " + type);
                }

                return result;
            }

            public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcRequest value, MessagePackSerializerOptions options)
            {
                writer.WriteArrayHeader(4);
                writer.Write(value.Version);
                options.Resolver.GetFormatterWithVerify<RequestId>().Serialize(ref writer, value.RequestId, options);
                writer.Write(value.Method);
                options.Resolver.GetFormatterWithVerify<object?>().Serialize(ref writer, value.Arguments, options);
            }
        }

        private class JsonRpcResultFormatter : IMessagePackFormatter<Protocol.JsonRpcResult>
        {
            internal static readonly JsonRpcResultFormatter Instance = new JsonRpcResultFormatter();

            private JsonRpcResultFormatter()
            {
            }

            public Protocol.JsonRpcResult Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                int arrayLength = reader.ReadArrayHeader();
                if (arrayLength != 3)
                {
                    throw new IOException("Unexpected length of result.");
                }

                return new JsonRpcResult
                {
                    Version = reader.ReadString(),
                    RequestId = options.Resolver.GetFormatterWithVerify<RequestId>().Deserialize(ref reader, options),
                    MsgPackResult = GetSliceForNextToken(ref reader),
                };
            }

            public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcResult value, MessagePackSerializerOptions options)
            {
                writer.WriteArrayHeader(3);
                writer.Write(value.Version);
                options.Resolver.GetFormatterWithVerify<RequestId>().Serialize(ref writer, value.RequestId, options);
                options.Resolver.GetFormatterWithVerify<object?>().Serialize(ref writer, value.Result, options);
            }
        }

        private class JsonRpcErrorDetailFormatter : IMessagePackFormatter<Protocol.JsonRpcError.ErrorDetail>
        {
            internal static readonly JsonRpcErrorDetailFormatter Instance = new JsonRpcErrorDetailFormatter();

            private JsonRpcErrorDetailFormatter()
            {
            }

            public Protocol.JsonRpcError.ErrorDetail Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                int arrayLength = reader.ReadArrayHeader();
                if (arrayLength != 3)
                {
                    throw new IOException("Unexpected length of result.");
                }

                return new JsonRpcError.ErrorDetail
                {
                    Code = options.Resolver.GetFormatterWithVerify<JsonRpcErrorCode>().Deserialize(ref reader, options),
                    Message = reader.ReadString(),
                    MsgPackData = GetSliceForNextToken(ref reader),
                };
            }

            public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcError.ErrorDetail value, MessagePackSerializerOptions options)
            {
                writer.WriteArrayHeader(3);
                options.Resolver.GetFormatterWithVerify<JsonRpcErrorCode>().Serialize(ref writer, value.Code, options);
                writer.Write(value.Message);
                options.Resolver.GetFormatterWithVerify<object?>().Serialize(ref writer, value.Data, options);
            }
        }

        [DataContract]
        private class JsonRpcRequest : Protocol.JsonRpcRequest, IJsonRpcMessageBufferManager
        {
            internal IReadOnlyDictionary<string, ReadOnlySequence<byte>>? MsgPackNamedArguments { get; set; }

            internal IReadOnlyList<ReadOnlySequence<byte>>? MsgPackPositionalArguments { get; set; }

            void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
            {
                Assumes.True(message == this);

                // Clear references to buffers that we are no longer entitled to.
                this.MsgPackNamedArguments = null;
                this.MsgPackPositionalArguments = null;
            }

            public override bool TryGetArgumentByNameOrIndex(string? name, int position, Type? typeHint, out object? value)
            {
                // If anyone asks us for an argument *after* we've been told deserialization is done, there's something very wrong.
                Assumes.True(this.MsgPackNamedArguments != null || this.MsgPackPositionalArguments != null);

                ReadOnlySequence<byte> msgpackArgument =
                    this.MsgPackPositionalArguments != null && position >= 0 ? this.MsgPackPositionalArguments[position] :
                    this.MsgPackNamedArguments != null && name is object ? this.MsgPackNamedArguments[name] :
                    default;

                if (msgpackArgument.IsEmpty)
                {
                    value = null;
                    return false;
                }

                var reader = new MessagePackReader(msgpackArgument);
                value = MessagePackSerializer.Deserialize(typeHint ?? typeof(object), ref reader, StandardOptions);
                return true;
            }
        }

        [DataContract]
        private class JsonRpcResult : Protocol.JsonRpcResult, IJsonRpcMessageBufferManager
        {
            internal ReadOnlySequence<byte> MsgPackResult { get; set; }

            void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
            {
                Assumes.True(message == this);
                this.MsgPackResult = default;
            }

            public override T GetResult<T>()
            {
                return this.MsgPackResult.IsEmpty
                    ? (T)this.Result!
                    : MessagePackSerializer.Deserialize<T>(this.MsgPackResult, StandardOptions);
            }

            protected override void SetExpectedResultType(Type resultType)
            {
                Verify.Operation(!this.MsgPackResult.IsEmpty, "Result is no longer available or has already been deserialized.");

                var reader = new MessagePackReader(this.MsgPackResult);
                this.Result = MessagePackSerializer.Deserialize(resultType, ref reader, StandardOptions);
                this.MsgPackResult = default;
            }
        }

        [DataContract]
        private class JsonRpcError : Protocol.JsonRpcError, IJsonRpcMessageBufferManager
        {
            void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
            {
                Assumes.True(message == this);
                if (this.Error is ErrorDetail privateDetail)
                {
                    privateDetail.MsgPackData = default;
                }
            }

            [DataContract]
            internal new class ErrorDetail : Protocol.JsonRpcError.ErrorDetail
            {
                internal ReadOnlySequence<byte> MsgPackData { get; set; }

                public override T GetData<T>()
                {
                    return this.MsgPackData.IsEmpty
                        ? (T)this.Data!
                        : MessagePackSerializer.Deserialize<T>(this.MsgPackData, StandardOptions);
                }

                protected override void SetExpectedResultType(Type dataType)
                {
                    Verify.Operation(!this.MsgPackData.IsEmpty, "Data is no longer available or has already been deserialized.");

                    var reader = new MessagePackReader(this.MsgPackData);
                    this.Data = MessagePackSerializer.Deserialize(dataType, ref reader, StandardOptions);
                    this.MsgPackData = default;
                }
            }
        }
    }
}
