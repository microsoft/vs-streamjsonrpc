// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.Serialization;
    using MessagePack;
    using MessagePack.Formatters;
    using MessagePack.Resolvers;
    using Microsoft;
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
        /// <summary>
        /// <see cref="MessageFormatterProgressTracker"/> instance containing useful methods to help on the implementation of message formatters.
        /// </summary>
        private readonly MessageFormatterProgressTracker formatterProgressTracker = new MessageFormatterProgressTracker();

        /// <summary>
        /// The options to use for serializing top-level RPC messages.
        /// </summary>
        private readonly MessagePackSerializerOptions messageSerializationOptions;

        /// <summary>
        /// The options to use for serializing user data (e.g. arguments, return values and errors).
        /// </summary>
        private MessagePackSerializerOptions userDataSerializationOptions = MessagePackSerializerOptions.Standard;

        /// <summary>
        /// The <see cref="IFormatterResolver"/> that was fed into the <see cref="GetWrappersForUserSuppliedResolver(IFormatterResolver, out MessagePackSerializerOptions, out IMessagePackFormatter{object?})"/> method.
        /// Backing field for the <see cref="Resolver"/> property.
        /// </summary>
        private IFormatterResolver userSuppliedResolver;

        /// <summary>
        /// The formatter to use when serializing user data that we only see typed as <see cref="object"/>.
        /// </summary>
        private IMessagePackFormatter<object?> dynamicObjectTypeFormatterForUserSuppliedResolver;

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcInstanceContainer.Rpc"/> property.
        /// </summary>
        private JsonRpc? rpc;

        /// <summary>
        /// Initializes a new instance of the <see cref="MessagePackFormatter"/> class.
        /// </summary>
        public MessagePackFormatter()
            : this(compress: false)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MessagePackFormatter"/> class.
        /// </summary>
        /// <param name="compress">A value indicating whether to use LZ4 compression.</param>
        private MessagePackFormatter(bool compress)
        {
            if (compress)
            {
                // Before we enable this, we need a way to ensure that the LZ4-expanded buffers stick around long enough for our deferred deserialization.
                // See https://github.com/neuecc/MessagePack-CSharp/issues/109#issuecomment-551370773
                throw new NotSupportedException();
            }

            // Set up initial options for our own message types.
            this.messageSerializationOptions = MessagePackSerializerOptions.Standard
                .WithLZ4Compression(useLZ4Compression: compress)
                .WithResolver(this.CreateTopLevelMessageResolver());

            // Set up default user data resolver.
            this.userSuppliedResolver = this.GetWrappersForUserSuppliedResolver(
                StandardResolverAllowPrivate.Instance,
                out this.userDataSerializationOptions,
                out this.dynamicObjectTypeFormatterForUserSuppliedResolver);
        }

        private interface IJsonRpcMessagePackRetention
        {
            /// <summary>
            /// Gets the original msgpack sequence that was deserialized into this message.
            /// </summary>
            /// <remarks>
            /// The buffer is only retained for a short time. If it has already been cleared, the result of this property is an empty sequence.
            /// </remarks>
            ReadOnlySequence<byte> OriginalMessagePack { get; }
        }

        /// <summary>
        /// Gets or sets the resolver to use for user data types.
        /// </summary>
        public IFormatterResolver Resolver
        {
            get => this.userSuppliedResolver;
            set
            {
                Requires.NotNull(value, nameof(value));
                if (value != this.userSuppliedResolver)
                {
                    this.userSuppliedResolver = this.GetWrappersForUserSuppliedResolver(value, out this.userDataSerializationOptions, out this.dynamicObjectTypeFormatterForUserSuppliedResolver);
                }
            }
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
        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer) => MessagePackSerializer.Deserialize<JsonRpcMessage>(contentBuffer, this.messageSerializationOptions);

        /// <inheritdoc/>
        public void Serialize(IBufferWriter<byte> contentBuffer, JsonRpcMessage message)
        {
            if (message is Protocol.JsonRpcRequest request && request.Arguments != null && request.ArgumentsList == null && !(request.Arguments is IReadOnlyDictionary<string, object?>))
            {
                // This request contains named arguments, but not using a standard dictionary. Convert it to a dictionary so that
                // the parameters can be matched to the method we're invoking.
                request.Arguments = GetParamsObjectDictionary(request.Arguments);
            }

            var writer = new MessagePackWriter(contentBuffer);
            MessagePackSerializer.Serialize(ref writer, message, this.messageSerializationOptions);
            writer.Flush();
        }

        /// <inheritdoc/>
        public object GetJsonText(JsonRpcMessage message) => message is IJsonRpcMessagePackRetention retainedMsgPack ? MessagePackSerializer.ConvertToJson(retainedMsgPack.OriginalMessagePack, this.messageSerializationOptions) : MessagePackSerializer.SerializeToJson(message, this.messageSerializationOptions);

        /// <summary>
        /// Extracts a dictionary of property names and values from the specified params object.
        /// </summary>
        /// <param name="paramsObject">The params object.</param>
        /// <returns>A dictionary, or <c>null</c> if <paramref name="paramsObject"/> is null.</returns>
        /// <remarks>
        /// This method supports DataContractSerializer-compliant types. This includes C# anonymous types.
        /// </remarks>
        [return: NotNullIfNotNull("paramsObject")]
        private static IReadOnlyDictionary<string, object?>? GetParamsObjectDictionary(object? paramsObject)
        {
            if (paramsObject == null)
            {
                return null;
            }

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);

            TypeInfo paramsTypeInfo = paramsObject.GetType().GetTypeInfo();
            bool isDataContract = paramsTypeInfo.GetCustomAttribute<DataContractAttribute>() != null;

            BindingFlags bindingFlags = BindingFlags.FlattenHierarchy | BindingFlags.Public | BindingFlags.Instance;
            if (isDataContract)
            {
                bindingFlags |= BindingFlags.NonPublic;
            }

            bool TryGetSerializationInfo(MemberInfo memberInfo, out string key)
            {
                key = memberInfo.Name;
                if (isDataContract)
                {
                    var dataMemberAttribute = memberInfo.GetCustomAttribute<DataMemberAttribute>();
                    if (dataMemberAttribute == null)
                    {
                        return false;
                    }

                    if (!dataMemberAttribute.EmitDefaultValue)
                    {
                        throw new NotSupportedException($"(DataMemberAttribute.EmitDefaultValue == false) is not supported but was found on: {memberInfo.DeclaringType.FullName}.{memberInfo.Name}.");
                    }

                    key = dataMemberAttribute.Name ?? memberInfo.Name;
                    return true;
                }
                else
                {
                    return memberInfo.GetCustomAttribute<IgnoreDataMemberAttribute>() == null;
                }
            }

            foreach (var property in paramsTypeInfo.GetProperties(bindingFlags))
            {
                if (property.GetMethod != null)
                {
                    if (TryGetSerializationInfo(property, out string key))
                    {
                        result[key] = property.GetValue(paramsObject);
                    }
                }
            }

            foreach (var field in paramsTypeInfo.GetFields(bindingFlags))
            {
                if (TryGetSerializationInfo(field, out string key))
                {
                    result[key] = field.GetValue(paramsObject);
                }
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

        /// <summary>
        /// Takes the user-supplied resolver for their data types and prepares the wrapping options
        /// and the dynamic object wrapper for serialization.
        /// </summary>
        /// <param name="resolver">The resolver supplied by the user, or the default.</param>
        /// <param name="options">Receives the new value for <see cref="userDataSerializationOptions"/> field.</param>
        /// <param name="dynamicObjectTypeFormatter">Receives the new value for the <see cref="dynamicObjectTypeFormatterForUserSuppliedResolver"/> field.</param>
        /// <returns>The new value for the <see cref="userSuppliedResolver"/> field. Simply a copy of the <paramref name="resolver"/> reference.</returns>
        private IFormatterResolver GetWrappersForUserSuppliedResolver(IFormatterResolver resolver, out MessagePackSerializerOptions options, out IMessagePackFormatter<object?> dynamicObjectTypeFormatter)
        {
            var formatters = new IMessagePackFormatter[]
            {
                // We preset this one in user data because $/cancellation methods can carry RequestId values as arguments.
                RequestIdFormatter.Instance,
            };
            var resolvers = new IFormatterResolver[]
            {
                resolver,
            };
            var userDataResolver = CompositeResolver.Create(formatters, resolvers);
            options = this.userDataSerializationOptions.WithResolver(userDataResolver);
            dynamicObjectTypeFormatter = new DynamicObjectTypeFallbackFormatter(this.userDataSerializationOptions.Resolver);
            return resolver;
        }

        private IFormatterResolver CreateTopLevelMessageResolver()
        {
            var formatters = new IMessagePackFormatter[]
            {
                RequestIdFormatter.Instance,
                JsonRpcMessageFormatter.Instance,
                new JsonRpcRequestFormatter(this),
                new JsonRpcResultFormatter(this),
                JsonRpcErrorFormatter.Instance,
                new JsonRpcErrorDetailFormatter(this),
            };
            var resolvers = new IFormatterResolver[]
            {
                StandardResolverAllowPrivate.Instance,
            };
            return CompositeResolver.Create(formatters, resolvers);
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
            private readonly MessagePackFormatter formatter;

            internal JsonRpcRequestFormatter(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public Protocol.JsonRpcRequest Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                int arrayLength = reader.ReadArrayHeader();
                if (arrayLength != 4)
                {
                    throw new IOException("Unexpected length of request.");
                }

                var result = new JsonRpcRequest(this.formatter.userDataSerializationOptions)
                {
                    OriginalMessagePack = reader.Sequence,
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
                    case MessagePackType.Nil:
                        result.MsgPackPositionalArguments = Array.Empty<ReadOnlySequence<byte>>();
                        reader.ReadNil();
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
                if (value.ArgumentsList != null)
                {
                    writer.WriteArrayHeader(value.ArgumentsList.Count);
                    foreach (var arg in value.ArgumentsList)
                    {
                        this.formatter.dynamicObjectTypeFormatterForUserSuppliedResolver.Serialize(ref writer, arg, this.formatter.userDataSerializationOptions);
                    }
                }
                else if (value.NamedArguments != null)
                {
                    writer.WriteMapHeader(value.NamedArguments.Count);
                    foreach (var entry in value.NamedArguments)
                    {
                        writer.Write(entry.Key);
                        this.formatter.dynamicObjectTypeFormatterForUserSuppliedResolver.Serialize(ref writer, entry.Value, this.formatter.userDataSerializationOptions);
                    }
                }
                else
                {
                    writer.WriteNil();
                }
            }
        }

        private class JsonRpcResultFormatter : IMessagePackFormatter<Protocol.JsonRpcResult>
        {
            private readonly MessagePackFormatter formatter;

            internal JsonRpcResultFormatter(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public Protocol.JsonRpcResult Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                int arrayLength = reader.ReadArrayHeader();
                if (arrayLength != 3)
                {
                    throw new IOException("Unexpected length of result.");
                }

                return new JsonRpcResult(this.formatter.userDataSerializationOptions)
                {
                    OriginalMessagePack = reader.Sequence,
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
                this.formatter.dynamicObjectTypeFormatterForUserSuppliedResolver.Serialize(ref writer, value.Result, this.formatter.userDataSerializationOptions);
            }
        }

        private class JsonRpcErrorFormatter : IMessagePackFormatter<Protocol.JsonRpcError>
        {
            internal static readonly JsonRpcErrorFormatter Instance = new JsonRpcErrorFormatter();

            private JsonRpcErrorFormatter()
            {
            }

            public Protocol.JsonRpcError Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                int arrayLength = reader.ReadArrayHeader();
                if (arrayLength != 3)
                {
                    throw new IOException("Unexpected length of result.");
                }

                return new JsonRpcError
                {
                    OriginalMessagePack = reader.Sequence,
                    Version = reader.ReadString(),
                    RequestId = options.Resolver.GetFormatterWithVerify<RequestId>().Deserialize(ref reader, options),
                    Error = options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError.ErrorDetail?>().Deserialize(ref reader, options),
                };
            }

            public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcError value, MessagePackSerializerOptions options)
            {
                writer.WriteArrayHeader(3);
                writer.Write(value.Version);
                options.Resolver.GetFormatterWithVerify<RequestId>().Serialize(ref writer, value.RequestId, options);
                options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError.ErrorDetail?>().Serialize(ref writer, value.Error, options);
            }
        }

        private class JsonRpcErrorDetailFormatter : IMessagePackFormatter<Protocol.JsonRpcError.ErrorDetail>
        {
            private readonly MessagePackFormatter formatter;

            internal JsonRpcErrorDetailFormatter(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public Protocol.JsonRpcError.ErrorDetail Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                int arrayLength = reader.ReadArrayHeader();
                if (arrayLength != 3)
                {
                    throw new IOException("Unexpected length of result.");
                }

                var result = new JsonRpcError.ErrorDetail(this.formatter.userDataSerializationOptions)
                {
                    Code = options.Resolver.GetFormatterWithVerify<JsonRpcErrorCode>().Deserialize(ref reader, options),
                    Message = reader.ReadString(),
                    MsgPackData = GetSliceForNextToken(ref reader),
                };
                return result;
            }

            public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcError.ErrorDetail value, MessagePackSerializerOptions options)
            {
                writer.WriteArrayHeader(3);
                options.Resolver.GetFormatterWithVerify<JsonRpcErrorCode>().Serialize(ref writer, value.Code, options);
                writer.Write(value.Message);
                this.formatter.dynamicObjectTypeFormatterForUserSuppliedResolver.Serialize(ref writer, value.Data, this.formatter.userDataSerializationOptions);
            }
        }

        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        [DataContract]
        private class JsonRpcRequest : Protocol.JsonRpcRequest, IJsonRpcMessageBufferManager, IJsonRpcMessagePackRetention
        {
            private readonly MessagePackSerializerOptions serializerOptions;

            internal JsonRpcRequest(MessagePackSerializerOptions serializerOptions)
            {
                this.serializerOptions = serializerOptions ?? throw new ArgumentNullException(nameof(serializerOptions));
            }

            public override int ArgumentCount => this.MsgPackNamedArguments?.Count ?? this.MsgPackPositionalArguments?.Count ?? base.ArgumentCount;

            public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

            internal IReadOnlyDictionary<string, ReadOnlySequence<byte>>? MsgPackNamedArguments { get; set; }

            internal IReadOnlyList<ReadOnlySequence<byte>>? MsgPackPositionalArguments { get; set; }

            void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
            {
                Assumes.True(message == this);

                // Clear references to buffers that we are no longer entitled to.
                this.MsgPackNamedArguments = null;
                this.MsgPackPositionalArguments = null;
                this.OriginalMessagePack = default;
            }

            public override bool TryGetArgumentByNameOrIndex(string? name, int position, Type? typeHint, out object? value)
            {
                // If anyone asks us for an argument *after* we've been told deserialization is done, there's something very wrong.
                Assumes.True(this.MsgPackNamedArguments != null || this.MsgPackPositionalArguments != null);

                ReadOnlySequence<byte> msgpackArgument = default;
                if (position >= 0 && this.MsgPackPositionalArguments?.Count > position)
                {
                    msgpackArgument = this.MsgPackPositionalArguments[position];
                }
                else if (name is object && this.MsgPackNamedArguments != null)
                {
                    this.MsgPackNamedArguments.TryGetValue(name, out msgpackArgument);
                }

                if (msgpackArgument.IsEmpty)
                {
                    value = null;
                    return false;
                }

                var reader = new MessagePackReader(msgpackArgument);
                try
                {
                    value = MessagePackSerializer.Deserialize(typeHint ?? typeof(object), ref reader, this.serializerOptions);
                    return true;
                }
                catch (MessagePackSerializationException)
                {
                    value = null;
                    return false;
                }
            }
        }

        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        [DataContract]
        private class JsonRpcResult : Protocol.JsonRpcResult, IJsonRpcMessageBufferManager, IJsonRpcMessagePackRetention
        {
            private readonly MessagePackSerializerOptions serializerOptions;

            internal JsonRpcResult(MessagePackSerializerOptions serializerOptions)
            {
                this.serializerOptions = serializerOptions ?? throw new ArgumentNullException(nameof(serializerOptions));
            }

            public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

            internal ReadOnlySequence<byte> MsgPackResult { get; set; }

            void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
            {
                Assumes.True(message == this);
                this.MsgPackResult = default;
                this.OriginalMessagePack = default;
            }

            public override T GetResult<T>()
            {
                return this.MsgPackResult.IsEmpty
                    ? (T)this.Result!
                    : MessagePackSerializer.Deserialize<T>(this.MsgPackResult, this.serializerOptions);
            }

            protected override void SetExpectedResultType(Type resultType)
            {
                Verify.Operation(!this.MsgPackResult.IsEmpty, "Result is no longer available or has already been deserialized.");

                var reader = new MessagePackReader(this.MsgPackResult);
                this.Result = MessagePackSerializer.Deserialize(resultType, ref reader, this.serializerOptions);
                this.MsgPackResult = default;
            }
        }

        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        [DataContract]
        private class JsonRpcError : Protocol.JsonRpcError, IJsonRpcMessageBufferManager, IJsonRpcMessagePackRetention
        {
            public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

            void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
            {
                Assumes.True(message == this);
                if (this.Error is ErrorDetail privateDetail)
                {
                    privateDetail.MsgPackData = default;
                }

                this.OriginalMessagePack = default;
            }

            [DataContract]
            internal new class ErrorDetail : Protocol.JsonRpcError.ErrorDetail
            {
                private readonly MessagePackSerializerOptions serializerOptions;

                internal ErrorDetail(MessagePackSerializerOptions serializerOptions)
                {
                    this.serializerOptions = serializerOptions ?? throw new ArgumentNullException(nameof(serializerOptions));
                }

                internal ReadOnlySequence<byte> MsgPackData { get; set; }

                public override object? GetData(Type dataType)
                {
                    Requires.NotNull(dataType, nameof(dataType));
                    if (this.MsgPackData.IsEmpty)
                    {
                        return this.Data;
                    }

                    var reader = new MessagePackReader(this.MsgPackData);
                    try
                    {
                        return MessagePackSerializer.Deserialize(dataType, ref reader, this.serializerOptions);
                    }
                    catch (MessagePackSerializationException)
                    {
                        // Deserialization failed. Try returning array/dictionary based primitive objects.
                        try
                        {
                            return MessagePackSerializer.Deserialize<object>(this.MsgPackData, this.serializerOptions.WithResolver(PrimitiveObjectResolver.Instance));
                        }
                        catch (MessagePackSerializationException)
                        {
                            return null;
                        }
                    }
                }

                protected override void SetExpectedDataType(Type dataType)
                {
                    Verify.Operation(!this.MsgPackData.IsEmpty, "Data is no longer available or has already been deserialized.");

                    this.Data = this.GetData(dataType);
                }
            }
        }
    }
}
