// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.IO.Pipelines;
    using System.Reflection;
    using System.Runtime.ExceptionServices;
    using System.Runtime.Serialization;
    using MessagePack;
    using MessagePack.Formatters;
    using MessagePack.Resolvers;
    using Microsoft;
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
    public class MessagePackFormatter : IJsonRpcMessageFormatter, IJsonRpcInstanceContainer, IJsonRpcFormatterState, IJsonRpcFormatterTracingCallbacks, IDisposable
    {
        /// <summary>
        /// The constant "jsonrpc".
        /// </summary>
        private const string VersionPropertyName = "jsonrpc";

        private const string IdPropertyName = "id";

        private const string MethodPropertyName = "method";

        private const string ResultPropertyName = "result";

        private const string ErrorPropertyName = "error";

        /// <summary>
        /// A cache of property names to declared property types, indexed by their containing parameter object type.
        /// </summary>
        /// <remarks>
        /// All access to this field should be while holding a lock on this member's value.
        /// </remarks>
        private static readonly Dictionary<Type, IReadOnlyDictionary<string, Type>> ParameterObjectPropertyTypes = new Dictionary<Type, IReadOnlyDictionary<string, Type>>();

        /// <summary>
        /// The options to use for serializing top-level RPC messages.
        /// </summary>
        private readonly MessagePackSerializerOptions messageSerializationOptions;

        private readonly ProgressFormatterResolver progressFormatterResolver;

        private readonly AsyncEnumerableFormatterResolver asyncEnumerableFormatterResolver;

        private readonly PipeFormatterResolver pipeFormatterResolver;

        /// <summary>
        /// Backing field for the <see cref="MultiplexingStream"/> property.
        /// </summary>
        private MultiplexingStream? multiplexingStream;

        /// <summary>
        /// The <see cref="MessageFormatterProgressTracker"/> we use to support <see cref="IProgress{T}"/> method arguments.
        /// </summary>
        private MessageFormatterProgressTracker? formatterProgressTracker;

        /// <summary>
        /// The helper for marshaling pipes as RPC method arguments.
        /// </summary>
        private MessageFormatterDuplexPipeTracker? duplexPipeTracker;

        /// <summary>
        /// The tracker we use to support transmission of <see cref="IAsyncEnumerable{T}"/> types.
        /// </summary>
        private MessageFormatterEnumerableTracker? enumerableTracker;

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcFormatterState.SerializingMessageWithId"/> property.
        /// </summary>
        private RequestId serializingMessageWithId;

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcFormatterState.DeserializingMessageWithId"/> property.
        /// </summary>
        private RequestId deserializingMessageWithId;

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcFormatterState.SerializingRequest"/> property.
        /// </summary>
        private bool serializingRequest;

        /// <summary>
        /// The options to use for serializing user data (e.g. arguments, return values and errors).
        /// </summary>
        private MessagePackSerializerOptions userDataSerializationOptions = MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

        /// <summary>
        /// Backing field for the <see cref="IJsonRpcInstanceContainer.Rpc"/> property.
        /// </summary>
        private JsonRpc? rpc;

        /// <summary>
        /// Initializes a new instance of the <see cref="MessagePackFormatter"/> class.
        /// </summary>
        public MessagePackFormatter()
        {
            // Set up initial options for our own message types.
            this.messageSerializationOptions = MessagePackSerializerOptions.Standard
                .WithSecurity(MessagePackSecurity.UntrustedData)
                .WithResolver(this.CreateTopLevelMessageResolver());

            // Create the specialized formatters/resolvers that we will inject into the chain for user data.
            this.progressFormatterResolver = new ProgressFormatterResolver(this);
            this.asyncEnumerableFormatterResolver = new AsyncEnumerableFormatterResolver(this);
            this.pipeFormatterResolver = new PipeFormatterResolver(this);

            // Set up default user data resolver.
            this.userDataSerializationOptions = this.MassageUserDataOptions(StandardResolverAllowPrivate.Options);
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

        /// <inheritdoc/>
        JsonRpc IJsonRpcInstanceContainer.Rpc
        {
            set
            {
                Verify.Operation(this.rpc == null, "This formatter already belongs to another JsonRpc instance. Create a new instance of this formatter for each new JsonRpc instance.");

                this.rpc = value;

                if (value != null)
                {
                    this.formatterProgressTracker = new MessageFormatterProgressTracker(value, this);
                    this.duplexPipeTracker = new MessageFormatterDuplexPipeTracker(value, this) { MultiplexingStream = this.multiplexingStream };
                    this.enumerableTracker = new MessageFormatterEnumerableTracker(value, this);
                }
            }
        }

        /// <summary>
        /// Gets or sets the <see cref="MultiplexingStream"/> that may be used to establish out of band communication (e.g. marshal <see cref="IDuplexPipe"/> arguments).
        /// </summary>
        public MultiplexingStream? MultiplexingStream
        {
            get => this.multiplexingStream;
            set
            {
                Verify.Operation(this.rpc == null, Resources.FormatterConfigurationLockedAfterJsonRpcAssigned);
                this.multiplexingStream = value;
            }
        }

        /// <inheritdoc/>
        RequestId IJsonRpcFormatterState.SerializingMessageWithId => this.serializingMessageWithId;

        /// <inheritdoc/>
        RequestId IJsonRpcFormatterState.DeserializingMessageWithId => this.deserializingMessageWithId;

        /// <inheritdoc/>
        bool IJsonRpcFormatterState.SerializingRequest => this.serializingRequest;

        /// <summary>
        /// Gets the <see cref="MessageFormatterProgressTracker"/> instance containing useful methods to help on the implementation of message formatters.
        /// </summary>
        private MessageFormatterProgressTracker FormatterProgressTracker
        {
            get
            {
                Assumes.NotNull(this.formatterProgressTracker); // This should have been set in the Rpc property setter.
                return this.formatterProgressTracker;
            }
        }

        /// <summary>
        /// Gets the helper for marshaling pipes as RPC method arguments.
        /// </summary>
        private MessageFormatterDuplexPipeTracker DuplexPipeTracker
        {
            get
            {
                Assumes.NotNull(this.duplexPipeTracker); // This should have been set in the Rpc property setter.
                return this.duplexPipeTracker;
            }
        }

        /// <summary>
        /// Gets the helper for marshaling <see cref="IAsyncEnumerable{T}"/> in RPC method arguments or return values.
        /// </summary>
        private MessageFormatterEnumerableTracker EnumerableTracker
        {
            get
            {
                Assumes.NotNull(this.enumerableTracker); // This should have been set in the Rpc property setter.
                return this.enumerableTracker;
            }
        }

        /// <summary>
        /// Sets the <see cref="MessagePackSerializerOptions"/> to use for serialization of user data.
        /// </summary>
        /// <param name="options">
        /// The options to use. Before this call, the options used come from <see cref="MessagePackSerializerOptions.Standard"/>
        /// modified to use the <see cref="MessagePackSecurity.UntrustedData"/> security setting.
        /// </param>
        public void SetMessagePackSerializerOptions(MessagePackSerializerOptions options)
        {
            Requires.NotNull(options, nameof(options));

            this.userDataSerializationOptions = this.MassageUserDataOptions(options);
        }

        /// <inheritdoc/>
        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer)
        {
            JsonRpcMessage message = MessagePackSerializer.Deserialize<JsonRpcMessage>(contentBuffer, this.messageSerializationOptions);

            IJsonRpcTracingCallbacks? tracingCallbacks = this.rpc;
            tracingCallbacks?.OnMessageDeserialized(message, new ToStringHelper(contentBuffer, this.messageSerializationOptions));

            return message;
        }

        /// <inheritdoc/>
        public void Serialize(IBufferWriter<byte> contentBuffer, JsonRpcMessage message)
        {
            if (message is Protocol.JsonRpcRequest request && request.Arguments != null && request.ArgumentsList == null && !(request.Arguments is IReadOnlyDictionary<string, object?>))
            {
                // This request contains named arguments, but not using a standard dictionary. Convert it to a dictionary so that
                // the parameters can be matched to the method we're invoking.
                if (GetParamsObjectDictionary(request.Arguments) is { } namedArgs)
                {
                    request.Arguments = namedArgs.ArgumentValues;
                    request.NamedArgumentDeclaredTypes = namedArgs.ArgumentTypes;
                }
            }

            var writer = new MessagePackWriter(contentBuffer);
            this.messageSerializationOptions.Resolver.GetFormatterWithVerify<JsonRpcMessage>().Serialize(ref writer, message, this.messageSerializationOptions);
            writer.Flush();
        }

        /// <inheritdoc/>
        public object GetJsonText(JsonRpcMessage message) => message is IJsonRpcMessagePackRetention retainedMsgPack ? MessagePackSerializer.ConvertToJson(retainedMsgPack.OriginalMessagePack, this.messageSerializationOptions) : throw new NotSupportedException();

        void IJsonRpcFormatterTracingCallbacks.OnSerializationComplete(JsonRpcMessage message, ReadOnlySequence<byte> encodedMessage)
        {
            IJsonRpcTracingCallbacks? tracingCallbacks = this.rpc;
            tracingCallbacks?.OnMessageSerialized(message, new ToStringHelper(encodedMessage, this.messageSerializationOptions));
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.duplexPipeTracker?.Dispose();
        }

        /// <summary>
        /// Extracts a dictionary of property names and values from the specified params object.
        /// </summary>
        /// <param name="paramsObject">The params object.</param>
        /// <returns>A dictionary of argument values and another of declared argument types, or <c>null</c> if <paramref name="paramsObject"/> is null.</returns>
        /// <remarks>
        /// This method supports DataContractSerializer-compliant types. This includes C# anonymous types.
        /// </remarks>
        [return: NotNullIfNotNull("paramsObject")]
        private static (IReadOnlyDictionary<string, object?> ArgumentValues, IReadOnlyDictionary<string, Type> ArgumentTypes)? GetParamsObjectDictionary(object? paramsObject)
        {
            if (paramsObject == null)
            {
                return default;
            }

            // Look up the argument types dictionary if we saved it before.
            Type paramsObjectType = paramsObject.GetType();
            IReadOnlyDictionary<string, Type>? argumentTypes;
            lock (ParameterObjectPropertyTypes)
            {
                ParameterObjectPropertyTypes.TryGetValue(paramsObjectType, out argumentTypes);
            }

            // If we couldn't find a previously created argument types dictionary, create a mutable one that we'll build this time.
            Dictionary<string, Type>? mutableArgumentTypes = argumentTypes is null ? new Dictionary<string, Type>() : null;

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
                    DataMemberAttribute dataMemberAttribute = memberInfo.GetCustomAttribute<DataMemberAttribute>();
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

            foreach (PropertyInfo property in paramsTypeInfo.GetProperties(bindingFlags))
            {
                if (property.GetMethod != null)
                {
                    if (TryGetSerializationInfo(property, out string key))
                    {
                        result[key] = property.GetValue(paramsObject);
                        if (mutableArgumentTypes is object)
                        {
                            mutableArgumentTypes[key] = property.PropertyType;
                        }
                    }
                }
            }

            foreach (FieldInfo field in paramsTypeInfo.GetFields(bindingFlags))
            {
                if (TryGetSerializationInfo(field, out string key))
                {
                    result[key] = field.GetValue(paramsObject);
                    if (mutableArgumentTypes is object)
                    {
                        mutableArgumentTypes[key] = field.FieldType;
                    }
                }
            }

            // If we assembled the argument types dictionary this time, save it for next time.
            if (mutableArgumentTypes is object)
            {
                lock (ParameterObjectPropertyTypes)
                {
                    if (ParameterObjectPropertyTypes.TryGetValue(paramsObjectType, out IReadOnlyDictionary<string, Type>? lostRace))
                    {
                        // Of the two, pick the winner to use ourselves so we consolidate on one and allow the GC to collect the loser sooner.
                        argumentTypes = lostRace;
                    }
                    else
                    {
                        ParameterObjectPropertyTypes.Add(paramsObjectType, argumentTypes = mutableArgumentTypes);
                    }
                }
            }

            return (result, argumentTypes!);
        }

        private static ReadOnlySequence<byte> GetSliceForNextToken(ref MessagePackReader reader)
        {
            SequencePosition startingPosition = reader.Position;
            reader.Skip();
            SequencePosition endingPosition = reader.Position;
            return reader.Sequence.Slice(startingPosition, endingPosition);
        }

        /// <summary>
        /// Takes the user-supplied resolver for their data types and prepares the wrapping options
        /// and the dynamic object wrapper for serialization.
        /// </summary>
        /// <param name="userSuppliedOptions">The options for user data that is supplied by the user (or the default).</param>
        /// <returns>The <see cref="MessagePackSerializerOptions"/> to use for all user data (args, return values and error data) and a special formatter to use when all we have is <see cref="object"/> for this user data.</returns>
        private MessagePackSerializerOptions MassageUserDataOptions(MessagePackSerializerOptions userSuppliedOptions)
        {
            var formatters = new IMessagePackFormatter[]
            {
                // We preset this one in user data because $/cancellation methods can carry RequestId values as arguments.
                RequestIdFormatter.Instance,

                // We preset this one because for some protocols like IProgress<T>, tokens are passed in that we must relay exactly back to the client as an argument.
                RawMessagePackFormatter.Instance,
            };
            var resolvers = new IFormatterResolver[]
            {
                userSuppliedOptions.Resolver,

                // Add our own resolvers to fill in specialized behavior if the user doesn't provide/override it by their own resolver.
                this.progressFormatterResolver,
                this.asyncEnumerableFormatterResolver,
                this.pipeFormatterResolver,
            };
            IFormatterResolver userDataResolver = CompositeResolver.Create(formatters, resolvers);

            MessagePackSerializerOptions userDataOptions = userSuppliedOptions
                .WithCompression(MessagePackCompression.None) // If/when we support LZ4 compression, it will be at the message level -- not the user-data level.
                .WithResolver(userDataResolver);

            return userDataOptions;
        }

        private IFormatterResolver CreateTopLevelMessageResolver()
        {
            var formatters = new IMessagePackFormatter[]
            {
                RequestIdFormatter.Instance,
                new JsonRpcMessageFormatter(this),
                new JsonRpcRequestFormatter(this),
                new JsonRpcResultFormatter(this),
                new JsonRpcErrorFormatter(this),
                new JsonRpcErrorDetailFormatter(this),
            };
            var resolvers = new IFormatterResolver[]
            {
                StandardResolverAllowPrivate.Instance,
            };
            return CompositeResolver.Create(formatters, resolvers);
        }

        private struct RawMessagePack
        {
            private readonly ReadOnlySequence<byte> rawSequence;

            private readonly ReadOnlyMemory<byte> rawMemory;

            private RawMessagePack(ReadOnlySequence<byte> raw)
            {
                this.rawSequence = raw;
                this.rawMemory = default;
            }

            private RawMessagePack(ReadOnlyMemory<byte> raw)
            {
                this.rawSequence = default;
                this.rawMemory = raw;
            }

            internal bool IsDefault => this.rawMemory.IsEmpty && this.rawSequence.IsEmpty;

            /// <summary>
            /// Reads one raw messagepack token.
            /// </summary>
            /// <param name="reader">The reader to use.</param>
            /// <param name="copy"><c>true</c> if the token must outlive the lifetime of the reader's underlying buffer; <c>false</c> otherwise.</param>
            /// <returns>The raw messagepack slice.</returns>
            internal static RawMessagePack ReadRaw(ref MessagePackReader reader, bool copy)
            {
                SequencePosition initialPosition = reader.Position;
                reader.Skip();
                ReadOnlySequence<byte> slice = reader.Sequence.Slice(initialPosition, reader.Position);
                return copy ? new RawMessagePack(slice.ToArray()) : new RawMessagePack(slice);
            }

            internal void WriteRaw(ref MessagePackWriter writer)
            {
                if (this.rawSequence.IsEmpty)
                {
                    writer.WriteRaw(this.rawMemory.Span);
                }
                else
                {
                    writer.WriteRaw(this.rawSequence);
                }
            }
        }

        private class ToStringHelper
        {
            private readonly ReadOnlySequence<byte> encodedMessage;
            private readonly MessagePackSerializerOptions options;

            internal ToStringHelper(ReadOnlySequence<byte> encodedMessage, MessagePackSerializerOptions options)
            {
                this.encodedMessage = encodedMessage;
                this.options = options;
            }

            public override string ToString() => MessagePackSerializer.ConvertToJson(this.encodedMessage, this.options);
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

        private class RawMessagePackFormatter : IMessagePackFormatter<RawMessagePack>
        {
            internal static readonly RawMessagePackFormatter Instance = new RawMessagePackFormatter();

            private RawMessagePackFormatter()
            {
            }

            public RawMessagePack Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                return RawMessagePack.ReadRaw(ref reader, copy: false);
            }

            public void Serialize(ref MessagePackWriter writer, RawMessagePack value, MessagePackSerializerOptions options)
            {
                value.WriteRaw(ref writer);
            }
        }

        private class ProgressFormatterResolver : IFormatterResolver
        {
            private readonly MessagePackFormatter mainFormatter;

            private readonly Dictionary<Type, IMessagePackFormatter?> progressFormatters = new Dictionary<Type, IMessagePackFormatter?>();

            internal ProgressFormatterResolver(MessagePackFormatter formatter)
            {
                this.mainFormatter = formatter;
            }

            public IMessagePackFormatter<T>? GetFormatter<T>()
            {
                lock (this.progressFormatters)
                {
                    if (!this.progressFormatters.TryGetValue(typeof(T), out IMessagePackFormatter? formatter))
                    {
                        if (typeof(T).IsConstructedGenericType && typeof(T).GetGenericTypeDefinition().Equals(typeof(IProgress<>)))
                        {
                            formatter = new PreciseTypeFormatter<T>(this.mainFormatter);
                        }
                        else if (MessageFormatterProgressTracker.IsSupportedProgressType(typeof(T)))
                        {
                            formatter = new ProgressClientFormatter<T>(this.mainFormatter);
                        }

                        this.progressFormatters.Add(typeof(T), formatter);
                    }

                    return (IMessagePackFormatter<T>?)formatter;
                }
            }

            /// <summary>
            /// Converts an instance of <see cref="IProgress{T}"/> to a progress token.
            /// </summary>
            private class ProgressClientFormatter<TClass> : IMessagePackFormatter<TClass>
            {
                private readonly MessagePackFormatter formatter;

                internal ProgressClientFormatter(MessagePackFormatter formatter)
                {
                    this.formatter = formatter;
                }

                public TClass Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    throw new NotSupportedException("This formatter only serializes IProgress<T> instances.");
                }

                public void Serialize(ref MessagePackWriter writer, TClass value, MessagePackSerializerOptions options)
                {
                    if (value is null)
                    {
                        writer.WriteNil();
                    }
                    else
                    {
                        long progressId = this.formatter.FormatterProgressTracker.GetTokenForProgress(value);
                        writer.Write(progressId);
                    }
                }
            }

            /// <summary>
            /// Converts a progress token to an <see cref="IProgress{T}"/> or an <see cref="IProgress{T}"/> into a token.
            /// </summary>
            private class PreciseTypeFormatter<TClass> : IMessagePackFormatter<TClass>
            {
                private readonly MessagePackFormatter formatter;

                internal PreciseTypeFormatter(MessagePackFormatter formatter)
                {
                    this.formatter = formatter;
                }

                [return: MaybeNull]
#pragma warning disable CS8766 // Nullability of reference types in return type doesn't match implicitly implemented member because of nullability attributes.
                public TClass Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
#pragma warning restore CS8766 // Nullability of reference types in return type doesn't match implicitly implemented member because of nullability attributes.
                {
                    if (reader.TryReadNil())
                    {
                        return default!;
                    }

                    Assumes.NotNull(this.formatter.rpc);
                    RawMessagePack token = RawMessagePack.ReadRaw(ref reader, copy: true);
                    return (TClass)this.formatter.FormatterProgressTracker.CreateProgress(this.formatter.rpc, token, typeof(TClass));
                }

                public void Serialize(ref MessagePackWriter writer, TClass value, MessagePackSerializerOptions options)
                {
                    if (value is null)
                    {
                        writer.WriteNil();
                    }
                    else
                    {
                        long progressId = this.formatter.FormatterProgressTracker.GetTokenForProgress(value);
                        writer.Write(progressId);
                    }
                }
            }
        }

        private class AsyncEnumerableFormatterResolver : IFormatterResolver
        {
            private readonly MessagePackFormatter mainFormatter;

            private readonly Dictionary<Type, IMessagePackFormatter?> enumerableFormatters = new Dictionary<Type, IMessagePackFormatter?>();

            internal AsyncEnumerableFormatterResolver(MessagePackFormatter formatter)
            {
                this.mainFormatter = formatter;
            }

            public IMessagePackFormatter<T>? GetFormatter<T>()
            {
                lock (this.enumerableFormatters)
                {
                    if (!this.enumerableFormatters.TryGetValue(typeof(T), out IMessagePackFormatter? formatter))
                    {
                        if (TrackerHelpers<IAsyncEnumerable<int>>.IsActualInterfaceMatch(typeof(T)))
                        {
                            formatter = (IMessagePackFormatter<T>?)Activator.CreateInstance(typeof(PreciseTypeFormatter<>).MakeGenericType(typeof(T).GenericTypeArguments[0]), new object[] { this.mainFormatter });
                        }
                        else if (TrackerHelpers<IAsyncEnumerable<int>>.FindInterfaceImplementedBy(typeof(T)) is { } iface)
                        {
                            formatter = (IMessagePackFormatter<T>?)Activator.CreateInstance(typeof(GeneratorFormatter<,>).MakeGenericType(typeof(T), iface.GenericTypeArguments[0]), new object[] { this.mainFormatter });
                        }

                        this.enumerableFormatters.Add(typeof(T), formatter);
                    }

                    return (IMessagePackFormatter<T>?)formatter;
                }
            }

            /// <summary>
            /// Converts an enumeration token to an <see cref="IAsyncEnumerable{T}"/>
            /// or an <see cref="IAsyncEnumerable{T}"/> into an enumeration token.
            /// </summary>
            private class PreciseTypeFormatter<T> : IMessagePackFormatter<IAsyncEnumerable<T>?>
            {
                private MessagePackFormatter mainFormatter;

                public PreciseTypeFormatter(MessagePackFormatter mainFormatter)
                {
                    this.mainFormatter = mainFormatter;
                }

                public IAsyncEnumerable<T>? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    if (reader.TryReadNil())
                    {
                        return default;
                    }

                    options.Security.DepthStep(ref reader);
                    RawMessagePack token = default;
                    IReadOnlyList<T>? initialElements = null;
                    int propertyCount = reader.ReadMapHeader();
                    for (int i = 0; i < propertyCount; i++)
                    {
                        switch (reader.ReadString())
                        {
                            case MessageFormatterEnumerableTracker.TokenPropertyName:
                                token = RawMessagePack.ReadRaw(ref reader, copy: true);
                                break;
                            case MessageFormatterEnumerableTracker.ValuesPropertyName:
                                initialElements = options.Resolver.GetFormatterWithVerify<IReadOnlyList<T>>().Deserialize(ref reader, options);
                                break;
                        }
                    }

                    reader.Depth--;
                    return this.mainFormatter.EnumerableTracker.CreateEnumerableProxy<T>(token.IsDefault ? null : (object)token, initialElements);
                }

                public void Serialize(ref MessagePackWriter writer, IAsyncEnumerable<T>? value, MessagePackSerializerOptions options)
                {
                    Serialize_Shared(this.mainFormatter, ref writer, value, options);
                }

                internal static MessagePackWriter Serialize_Shared(MessagePackFormatter mainFormatter, ref MessagePackWriter writer, IAsyncEnumerable<T>? value, MessagePackSerializerOptions options)
                {
                    if (value is null)
                    {
                        writer.WriteNil();
                    }
                    else
                    {
                        (IReadOnlyList<T> Elements, bool Finished) prefetched = value.TearOffPrefetchedElements();
                        long token = mainFormatter.EnumerableTracker.GetToken(value);

                        int propertyCount = 0;
                        if (prefetched.Elements.Count > 0)
                        {
                            propertyCount++;
                        }

                        if (!prefetched.Finished)
                        {
                            propertyCount++;
                        }

                        writer.WriteMapHeader(propertyCount);

                        if (!prefetched.Finished)
                        {
                            writer.Write(MessageFormatterEnumerableTracker.TokenPropertyName);
                            writer.Write(token);
                        }

                        if (prefetched.Elements.Count > 0)
                        {
                            writer.Write(MessageFormatterEnumerableTracker.ValuesPropertyName);
                            options.Resolver.GetFormatterWithVerify<IReadOnlyList<T>>().Serialize(ref writer, prefetched.Elements, options);
                        }
                    }

                    return writer;
                }
            }

            /// <summary>
            /// Converts an instance of <see cref="IAsyncEnumerable{T}"/> to an enumeration token.
            /// </summary>
            private class GeneratorFormatter<TClass, TElement> : IMessagePackFormatter<TClass>
                where TClass : IAsyncEnumerable<TElement>
            {
                private MessagePackFormatter mainFormatter;

                public GeneratorFormatter(MessagePackFormatter mainFormatter)
                {
                    this.mainFormatter = mainFormatter;
                }

                public TClass Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    throw new NotSupportedException();
                }

                public void Serialize(ref MessagePackWriter writer, TClass value, MessagePackSerializerOptions options)
                {
                    writer = PreciseTypeFormatter<TElement>.Serialize_Shared(this.mainFormatter, ref writer, value, options);
                }
            }
        }

        private class PipeFormatterResolver : IFormatterResolver
        {
            private readonly MessagePackFormatter mainFormatter;

            private readonly Dictionary<Type, IMessagePackFormatter?> pipeFormatters = new Dictionary<Type, IMessagePackFormatter?>();

            internal PipeFormatterResolver(MessagePackFormatter formatter)
            {
                this.mainFormatter = formatter;
            }

            public IMessagePackFormatter<T>? GetFormatter<T>()
            {
                lock (this.pipeFormatters)
                {
                    if (!this.pipeFormatters.TryGetValue(typeof(T), out IMessagePackFormatter? formatter))
                    {
                        if (typeof(IDuplexPipe).IsAssignableFrom(typeof(T)))
                        {
                            formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(DuplexPipeFormatter<>).MakeGenericType(typeof(T)), this.mainFormatter);
                        }
                        else if (typeof(PipeReader).IsAssignableFrom(typeof(T)))
                        {
                            formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(PipeReaderFormatter<>).MakeGenericType(typeof(T)), this.mainFormatter);
                        }
                        else if (typeof(PipeWriter).IsAssignableFrom(typeof(T)))
                        {
                            formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(PipeWriterFormatter<>).MakeGenericType(typeof(T)), this.mainFormatter);
                        }
                        else if (typeof(Stream).IsAssignableFrom(typeof(T)))
                        {
                            formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(StreamFormatter<>).MakeGenericType(typeof(T)), this.mainFormatter);
                        }

                        this.pipeFormatters.Add(typeof(T), formatter);
                    }

                    return (IMessagePackFormatter<T>?)formatter;
                }
            }

            private class DuplexPipeFormatter<T> : IMessagePackFormatter<T?>
                where T : class, IDuplexPipe
            {
                private readonly MessagePackFormatter formatter;

                public DuplexPipeFormatter(MessagePackFormatter formatter)
                {
                    this.formatter = formatter;
                }

                public T? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    if (reader.TryReadNil())
                    {
                        return null;
                    }

                    return (T)this.formatter.DuplexPipeTracker.GetPipe(reader.ReadInt32());
                }

                public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
                {
                    if (this.formatter.DuplexPipeTracker.GetToken(value) is { } token)
                    {
                        writer.Write(token);
                    }
                    else
                    {
                        writer.WriteNil();
                    }
                }
            }

            private class PipeReaderFormatter<T> : IMessagePackFormatter<T?>
                where T : PipeReader
            {
                private readonly MessagePackFormatter formatter;

                public PipeReaderFormatter(MessagePackFormatter formatter)
                {
                    this.formatter = formatter;
                }

                public T? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    if (reader.TryReadNil())
                    {
                        return null;
                    }

                    return (T)this.formatter.DuplexPipeTracker.GetPipeReader(reader.ReadInt32());
                }

                public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
                {
                    if (this.formatter.DuplexPipeTracker.GetToken(value) is { } token)
                    {
                        writer.Write(token);
                    }
                    else
                    {
                        writer.WriteNil();
                    }
                }
            }

            private class PipeWriterFormatter<T> : IMessagePackFormatter<T?>
                where T : PipeWriter
            {
                private readonly MessagePackFormatter formatter;

                public PipeWriterFormatter(MessagePackFormatter formatter)
                {
                    this.formatter = formatter;
                }

                public T? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    if (reader.TryReadNil())
                    {
                        return null;
                    }

                    return (T)this.formatter.DuplexPipeTracker.GetPipeWriter(reader.ReadInt32());
                }

                public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
                {
                    if (this.formatter.DuplexPipeTracker.GetToken(value) is { } token)
                    {
                        writer.Write(token);
                    }
                    else
                    {
                        writer.WriteNil();
                    }
                }
            }

            private class StreamFormatter<T> : IMessagePackFormatter<T?>
                where T : Stream
            {
                private readonly MessagePackFormatter formatter;

                public StreamFormatter(MessagePackFormatter formatter)
                {
                    this.formatter = formatter;
                }

                public T? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
                {
                    if (reader.TryReadNil())
                    {
                        return null;
                    }

                    return (T)this.formatter.DuplexPipeTracker.GetPipe(reader.ReadInt32()).AsStream();
                }

                public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
                {
                    if (this.formatter.DuplexPipeTracker.GetToken(value?.UsePipe()) is { } token)
                    {
                        writer.Write(token);
                    }
                    else
                    {
                        writer.WriteNil();
                    }
                }
            }
        }

        private class JsonRpcMessageFormatter : IMessagePackFormatter<JsonRpcMessage>
        {
            private readonly MessagePackFormatter formatter;

            internal JsonRpcMessageFormatter(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public JsonRpcMessage Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                MessagePackReader readAhead = reader.CreatePeekReader();
                int propertyCount = readAhead.ReadMapHeader();
                for (int i = 0; i < propertyCount; i++)
                {
                    string propertyName = readAhead.ReadString();
                    if (propertyName == MethodPropertyName)
                    {
                        return options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcRequest>().Deserialize(ref reader, options);
                    }
                    else if (propertyName == ResultPropertyName)
                    {
                        return options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcResult>().Deserialize(ref reader, options);
                    }
                    else if (propertyName == ErrorPropertyName)
                    {
                        return options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError>().Deserialize(ref reader, options);
                    }

                    // Skip over the entire value of this property.
                    readAhead.Skip();
                }

                throw new UnrecognizedJsonRpcMessageException();
            }

            public void Serialize(ref MessagePackWriter writer, JsonRpcMessage value, MessagePackSerializerOptions options)
            {
                Requires.NotNull(value, nameof(value));

                this.formatter.serializingMessageWithId = value is IJsonRpcMessageWithId msgWithId ? msgWithId.RequestId : default;
                try
                {
                    switch (value)
                    {
                        case Protocol.JsonRpcRequest request:
                            this.formatter.serializingRequest = true;
                            options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcRequest>().Serialize(ref writer, request, options);
                            break;
                        case Protocol.JsonRpcResult result:
                            options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcResult>().Serialize(ref writer, result, options);
                            break;
                        case Protocol.JsonRpcError error:
                            options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError>().Serialize(ref writer, error, options);
                            break;
                        default:
                            throw new NotSupportedException("Unexpected JsonRpcMessage-derived type: " + value.GetType().Name);
                    }
                }
                finally
                {
                    this.formatter.serializingMessageWithId = default;
                    this.formatter.serializingRequest = false;
                }
            }
        }

        private class JsonRpcRequestFormatter : IMessagePackFormatter<Protocol.JsonRpcRequest>
        {
            private const string ParamsPropertyName = "params";

            private readonly MessagePackFormatter formatter;

            internal JsonRpcRequestFormatter(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public Protocol.JsonRpcRequest Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                var result = new JsonRpcRequest(this.formatter)
                {
                    OriginalMessagePack = reader.Sequence,
                };

                options.Security.DepthStep(ref reader);
                int propertyCount = reader.ReadMapHeader();
                for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
                {
                    switch (reader.ReadString())
                    {
                        case VersionPropertyName:
                            result.Version = reader.ReadString();
                            break;
                        case IdPropertyName:
                            result.RequestId = options.Resolver.GetFormatterWithVerify<RequestId>().Deserialize(ref reader, options);
                            break;
                        case MethodPropertyName:
                            result.Method = reader.ReadString();
                            break;
                        case ParamsPropertyName:
                            SequencePosition paramsTokenStartPosition = reader.Position;

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

                            result.MsgPackArguments = reader.Sequence.Slice(paramsTokenStartPosition, reader.Position);

                            break;
                    }
                }

                // If method is $/progress, get the progress instance from the dictionary and call Report
                if (string.Equals(result.Method, MessageFormatterProgressTracker.ProgressRequestSpecialMethod, StringComparison.Ordinal))
                {
                    try
                    {
                        if (result.TryGetArgumentByNameOrIndex("token", 0, typeof(long), out object? tokenObject) && tokenObject is long progressId)
                        {
                            MessageFormatterProgressTracker.ProgressParamInformation? progressInfo = null;
                            if (this.formatter.FormatterProgressTracker.TryGetProgressObject(progressId, out progressInfo))
                            {
                                if (result.TryGetArgumentByNameOrIndex("value", 1, progressInfo.ValueType, out object? value))
                                {
                                    progressInfo.InvokeReport(value);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        this.formatter.rpc?.TraceSource.TraceData(TraceEventType.Error, (int)JsonRpc.TraceEvents.ProgressNotificationError, ex);
                    }
                }

                reader.Depth--;
                return result;
            }

            public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcRequest value, MessagePackSerializerOptions options)
            {
                writer.WriteMapHeader(4);

                writer.Write(VersionPropertyName);
                writer.Write(value.Version);

                writer.Write(IdPropertyName);
                options.Resolver.GetFormatterWithVerify<RequestId>().Serialize(ref writer, value.RequestId, options);

                writer.Write(MethodPropertyName);
                writer.Write(value.Method);

                writer.Write(ParamsPropertyName);

                if (value.ArgumentsList != null)
                {
                    writer.WriteArrayHeader(value.ArgumentsList.Count);
                    for (int i = 0; i < value.ArgumentsList.Count; i++)
                    {
                        object? arg = value.ArgumentsList[i];
                        if (value.ArgumentListDeclaredTypes is object)
                        {
                            MessagePackSerializer.Serialize(value.ArgumentListDeclaredTypes[i], ref writer, arg, this.formatter.userDataSerializationOptions);
                        }
                        else
                        {
                            DynamicObjectTypeFallbackFormatter.Instance.Serialize(ref writer, arg, this.formatter.userDataSerializationOptions);
                        }
                    }
                }
                else if (value.NamedArguments != null)
                {
                    writer.WriteMapHeader(value.NamedArguments.Count);
                    foreach (KeyValuePair<string, object?> entry in value.NamedArguments)
                    {
                        writer.Write(entry.Key);
                        if (value.NamedArgumentDeclaredTypes?[entry.Key] is Type argType)
                        {
                            MessagePackSerializer.Serialize(argType, ref writer, entry.Value, this.formatter.userDataSerializationOptions);
                        }
                        else
                        {
                            DynamicObjectTypeFallbackFormatter.Instance.Serialize(ref writer, entry.Value, this.formatter.userDataSerializationOptions);
                        }
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
                var result = new JsonRpcResult(this.formatter.userDataSerializationOptions)
                {
                    OriginalMessagePack = reader.Sequence,
                };

                options.Security.DepthStep(ref reader);
                int propertyCount = reader.ReadMapHeader();
                for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
                {
                    switch (reader.ReadString())
                    {
                        case VersionPropertyName:
                            result.Version = reader.ReadString();
                            break;
                        case IdPropertyName:
                            result.RequestId = options.Resolver.GetFormatterWithVerify<RequestId>().Deserialize(ref reader, options);
                            break;
                        case ResultPropertyName:
                            result.MsgPackResult = GetSliceForNextToken(ref reader);
                            break;
                    }
                }

                reader.Depth--;
                return result;
            }

            public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcResult value, MessagePackSerializerOptions options)
            {
                writer.WriteMapHeader(3);

                writer.Write(VersionPropertyName);
                writer.Write(value.Version);

                writer.Write(IdPropertyName);
                options.Resolver.GetFormatterWithVerify<RequestId>().Serialize(ref writer, value.RequestId, options);

                writer.Write(ResultPropertyName);
                if (value.ResultDeclaredType is object && value.ResultDeclaredType != typeof(void))
                {
                    MessagePackSerializer.Serialize(value.ResultDeclaredType, ref writer, value.Result, this.formatter.userDataSerializationOptions);
                }
                else
                {
                    DynamicObjectTypeFallbackFormatter.Instance.Serialize(ref writer, value.Result, this.formatter.userDataSerializationOptions);
                }
            }
        }

        private class JsonRpcErrorFormatter : IMessagePackFormatter<Protocol.JsonRpcError>
        {
            private readonly MessagePackFormatter formatter;

            internal JsonRpcErrorFormatter(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public Protocol.JsonRpcError Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                var error = new JsonRpcError
                {
                    OriginalMessagePack = reader.Sequence,
                };

                options.Security.DepthStep(ref reader);
                int propertyCount = reader.ReadMapHeader();
                for (int propertyIdx = 0; propertyIdx < propertyCount; propertyIdx++)
                {
                    switch (reader.ReadString())
                    {
                        case VersionPropertyName:
                            error.Version = reader.ReadString();
                            break;
                        case IdPropertyName:
                            error.RequestId = options.Resolver.GetFormatterWithVerify<RequestId>().Deserialize(ref reader, options);
                            break;
                        case ErrorPropertyName:
                            error.Error = options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError.ErrorDetail?>().Deserialize(ref reader, options);
                            break;
                    }
                }

                reader.Depth--;
                return error;
            }

            public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcError value, MessagePackSerializerOptions options)
            {
                writer.WriteMapHeader(3);

                writer.Write(VersionPropertyName);
                writer.Write(value.Version);

                writer.Write(IdPropertyName);
                options.Resolver.GetFormatterWithVerify<RequestId>().Serialize(ref writer, value.RequestId, options);

                writer.Write(ErrorPropertyName);
                options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError.ErrorDetail?>().Serialize(ref writer, value.Error, options);
            }
        }

        private class JsonRpcErrorDetailFormatter : IMessagePackFormatter<Protocol.JsonRpcError.ErrorDetail>
        {
            private const string CodePropertyName = "code";
            private const string MessagePropertyName = "message";
            private const string DataPropertyName = "data";
            private readonly MessagePackFormatter formatter;

            internal JsonRpcErrorDetailFormatter(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public Protocol.JsonRpcError.ErrorDetail Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                var result = new JsonRpcError.ErrorDetail(this.formatter.userDataSerializationOptions);

                options.Security.DepthStep(ref reader);
                int propertyCount = reader.ReadMapHeader();
                for (int propertyIdx = 0; propertyIdx < propertyCount; propertyIdx++)
                {
                    switch (reader.ReadString())
                    {
                        case CodePropertyName:
                            result.Code = options.Resolver.GetFormatterWithVerify<JsonRpcErrorCode>().Deserialize(ref reader, options);
                            break;
                        case MessagePropertyName:
                            result.Message = reader.ReadString();
                            break;
                        case DataPropertyName:
                            result.MsgPackData = GetSliceForNextToken(ref reader);
                            break;
                    }
                }

                reader.Depth--;
                return result;
            }

            public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcError.ErrorDetail value, MessagePackSerializerOptions options)
            {
                writer.WriteMapHeader(3);

                writer.Write(CodePropertyName);
                options.Resolver.GetFormatterWithVerify<JsonRpcErrorCode>().Serialize(ref writer, value.Code, options);

                writer.Write(MessagePropertyName);
                writer.Write(value.Message);

                writer.Write(DataPropertyName);
                DynamicObjectTypeFallbackFormatter.Instance.Serialize(ref writer, value.Data, this.formatter.userDataSerializationOptions);
            }
        }

        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        [DataContract]
        private class JsonRpcRequest : Protocol.JsonRpcRequest, IJsonRpcMessageBufferManager, IJsonRpcMessagePackRetention
        {
            private readonly MessagePackFormatter formatter;

            internal JsonRpcRequest(MessagePackFormatter formatter)
            {
                this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
            }

            public override int ArgumentCount => this.MsgPackNamedArguments?.Count ?? this.MsgPackPositionalArguments?.Count ?? base.ArgumentCount;

            public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

            internal ReadOnlySequence<byte> MsgPackArguments { get; set; }

            internal IReadOnlyDictionary<string, ReadOnlySequence<byte>>? MsgPackNamedArguments { get; set; }

            internal IReadOnlyList<ReadOnlySequence<byte>>? MsgPackPositionalArguments { get; set; }

            void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
            {
                Assumes.True(message == this);

                // Clear references to buffers that we are no longer entitled to.
                this.MsgPackNamedArguments = null;
                this.MsgPackPositionalArguments = null;
                this.MsgPackArguments = default;
                this.OriginalMessagePack = default;
            }

            public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, Span<object?> typedArguments)
            {
                if (parameters.Length == 1 && this.MsgPackNamedArguments != null)
                {
                    Assumes.NotNull(this.Method);

                    JsonRpcMethodAttribute? attribute = this.formatter.rpc?.GetJsonRpcMethodAttribute(this.Method, parameters);
                    if (attribute?.UseSingleObjectParameterDeserialization ?? false)
                    {
                        var reader = new MessagePackReader(this.MsgPackArguments);
                        try
                        {
                            typedArguments[0] = MessagePackSerializer.Deserialize(parameters[0].ParameterType, ref reader, this.formatter.userDataSerializationOptions);
                            return ArgumentMatchResult.Success;
                        }
                        catch (MessagePackSerializationException)
                        {
                            return ArgumentMatchResult.ParameterArgumentTypeMismatch;
                        }
                    }
                }

                return base.TryGetTypedArguments(parameters, typedArguments);
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
                    // Deserialization of messages should never occur concurrently for a single instance of a formatter.
                    Assumes.True(this.formatter.deserializingMessageWithId.IsEmpty);
                    this.formatter.deserializingMessageWithId = this.RequestId;

                    value = MessagePackSerializer.Deserialize(typeHint ?? typeof(object), ref reader, this.formatter.userDataSerializationOptions);
                    return true;
                }
                catch (MessagePackSerializationException ex)
                {
                    if (this.formatter.rpc?.TraceSource.Switch.ShouldTrace(TraceEventType.Warning) ?? false)
                    {
                        this.formatter.rpc.TraceSource.TraceEvent(TraceEventType.Warning, (int)JsonRpc.TraceEvents.MethodArgumentDeserializationFailure, Resources.FailureDeserializingRpcArgument, name, position, typeHint, ex);
                    }

                    throw new RpcArgumentDeserializationException(name, position, typeHint, ex);
                }
                finally
                {
                    this.formatter.deserializingMessageWithId = default;
                }
            }
        }

        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        [DataContract]
        private class JsonRpcResult : Protocol.JsonRpcResult, IJsonRpcMessageBufferManager, IJsonRpcMessagePackRetention
        {
            private readonly MessagePackSerializerOptions serializerOptions;

            private Exception? resultDeserializationException;

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
                if (this.resultDeserializationException != null)
                {
                    ExceptionDispatchInfo.Capture(this.resultDeserializationException).Throw();
                }

                return this.MsgPackResult.IsEmpty
                    ? (T)this.Result!
                    : MessagePackSerializer.Deserialize<T>(this.MsgPackResult, this.serializerOptions);
            }

            protected internal override void SetExpectedResultType(Type resultType)
            {
                Verify.Operation(!this.MsgPackResult.IsEmpty, "Result is no longer available or has already been deserialized.");

                var reader = new MessagePackReader(this.MsgPackResult);
                try
                {
                    this.Result = MessagePackSerializer.Deserialize(resultType, ref reader, this.serializerOptions);
                    this.MsgPackResult = default;
                }
                catch (MessagePackSerializationException ex)
                {
                    // This was a best effort anyway. We'll throw again later at a more convenient time for JsonRpc.
                    this.resultDeserializationException = ex;
                }
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

                protected internal override void SetExpectedDataType(Type dataType)
                {
                    Verify.Operation(!this.MsgPackData.IsEmpty, "Data is no longer available or has already been deserialized.");

                    this.Data = this.GetData(dataType);
                }
            }
        }
    }
}
