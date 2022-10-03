﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Text;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using Nerdbank.Streams;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
/// <remarks>
/// The MessagePack implementation used here comes from https://github.com/neuecc/MessagePack-CSharp.
/// The README on that project site describes use cases and its performance compared to alternative
/// .NET MessagePack implementations and this one appears to be the best by far.
/// </remarks>
public class MessagePackFormatter : IJsonRpcMessageFormatter, IJsonRpcInstanceContainer, IJsonRpcFormatterState, IJsonRpcFormatterTracingCallbacks, IJsonRpcMessageFactory, IDisposable
{
    /// <summary>
    /// The constant "jsonrpc", in its various forms.
    /// </summary>
    private static readonly CommonString VersionPropertyName = new CommonString(Constants.jsonrpc);

    /// <summary>
    /// The constant "id", in its various forms.
    /// </summary>
    private static readonly CommonString IdPropertyName = new CommonString(Constants.id);

    /// <summary>
    /// The constant "method", in its various forms.
    /// </summary>
    private static readonly CommonString MethodPropertyName = new CommonString(Constants.Request.method);

    /// <summary>
    /// The constant "result", in its various forms.
    /// </summary>
    private static readonly CommonString ResultPropertyName = new CommonString(Constants.Result.result);

    /// <summary>
    /// The constant "error", in its various forms.
    /// </summary>
    private static readonly CommonString ErrorPropertyName = new CommonString(Constants.Error.error);

    /// <summary>
    /// The constant "params", in its various forms.
    /// </summary>
    private static readonly CommonString ParamsPropertyName = new CommonString(Constants.Request.@params);

    /// <summary>
    /// The constant "traceparent", in its various forms.
    /// </summary>
    private static readonly CommonString TraceParentPropertyName = new CommonString(Constants.Request.traceparent);

    /// <summary>
    /// The constant "tracestate", in its various forms.
    /// </summary>
    private static readonly CommonString TraceStatePropertyName = new CommonString(Constants.Request.tracestate);

    /// <summary>
    /// The constant "2.0", in its various forms.
    /// </summary>
    private static readonly CommonString Version2 = new CommonString("2.0");

    /// <summary>
    /// A cache of property names to declared property types, indexed by their containing parameter object type.
    /// </summary>
    /// <remarks>
    /// All access to this field should be while holding a lock on this member's value.
    /// </remarks>
    private static readonly Dictionary<Type, IReadOnlyDictionary<string, Type>> ParameterObjectPropertyTypes = new Dictionary<Type, IReadOnlyDictionary<string, Type>>();

    /// <summary>
    /// A resolver for stateless formatters that make types serializable that users may expect to be,
    /// but for which MessagePack itself provides no formatter in the default resolver.
    /// </summary>
    private static readonly IFormatterResolver BasicTypesResolver = CompositeResolver.Create(
        EventArgsFormatter.Instance);

    /// <summary>
    /// The options to use for serializing top-level RPC messages.
    /// </summary>
    private readonly MessagePackSerializerOptions messageSerializationOptions;

    private readonly ProgressFormatterResolver progressFormatterResolver;

    private readonly AsyncEnumerableFormatterResolver asyncEnumerableFormatterResolver;

    private readonly PipeFormatterResolver pipeFormatterResolver;

    private readonly MessagePackExceptionResolver exceptionResolver;

    private readonly ToStringHelper serializationToStringHelper = new ToStringHelper();

    private readonly ToStringHelper deserializationToStringHelper = new ToStringHelper();

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
    /// The helper for marshaling <see cref="IRpcMarshaledContext{T}"/> in RPC method arguments or return values.
    /// </summary>
    private MessageFormatterRpcMarshaledContextTracker? rpcMarshaledContextTracker;

    /// <summary>
    /// Backing field for the <see cref="IJsonRpcFormatterState.SerializingMessageWithId"/> property.
    /// </summary>
    private RequestId serializingMessageWithId;

    /// <summary>
    /// Backing field for the <see cref="IJsonRpcFormatterState.DeserializingMessageWithId"/> property.
    /// </summary>
    private RequestId deserializingMessageWithId;

    /// <summary>
    /// The message whose arguments are being deserialized.
    /// </summary>
    private JsonRpcMessage? deserializingMessage;

    /// <summary>
    /// Backing field for the <see cref="IJsonRpcFormatterState.SerializingRequest"/> property.
    /// </summary>
    private bool serializingRequest;

    /// <summary>
    /// The options to use for serializing user data (e.g. arguments, return values and errors).
    /// </summary>
    private MessagePackSerializerOptions userDataSerializationOptions;

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
        this.exceptionResolver = new MessagePackExceptionResolver(this);

        // Set up default user data resolver.
        this.userDataSerializationOptions = this.MassageUserDataOptions(DefaultUserDataSerializationOptions);
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

    private interface IMessageWithTopLevelPropertyBag
    {
        TopLevelPropertyBag? TopLevelPropertyBag { get; set; }
    }

    /// <summary>
    /// Gets the default <see cref="MessagePackSerializerOptions"/> used for user data (arguments, return values and errors) in RPC calls
    /// prior to any call to <see cref="SetMessagePackSerializerOptions(MessagePackSerializerOptions)"/>.
    /// </summary>
    /// <value>
    /// This is <see cref="StandardResolverAllowPrivate.Options"/>
    /// modified to use the <see cref="MessagePackSecurity.UntrustedData"/> security setting.
    /// </value>
    public static MessagePackSerializerOptions DefaultUserDataSerializationOptions { get; } = StandardResolverAllowPrivate.Options
        .WithSecurity(MessagePackSecurity.UntrustedData);

    /// <inheritdoc/>
    JsonRpc IJsonRpcInstanceContainer.Rpc
    {
        set
        {
            Verify.Operation(this.rpc is null, "This formatter already belongs to another JsonRpc instance. Create a new instance of this formatter for each new JsonRpc instance.");

            this.rpc = value;

            if (value is not null)
            {
                this.formatterProgressTracker = new MessageFormatterProgressTracker(value, this);
                this.duplexPipeTracker = new MessageFormatterDuplexPipeTracker(value, this) { MultiplexingStream = this.multiplexingStream };
                this.enumerableTracker = new MessageFormatterEnumerableTracker(value, this);
                this.rpcMarshaledContextTracker = new MessageFormatterRpcMarshaledContextTracker(value, this);
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
            Verify.Operation(this.rpc is null, Resources.FormatterConfigurationLockedAfterJsonRpcAssigned);
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
    /// Gets the helper for marshaling <see cref="IRpcMarshaledContext{T}"/> in RPC method arguments or return values.
    /// </summary>
    private MessageFormatterRpcMarshaledContextTracker RpcMarshaledContextTracker
    {
        get
        {
            Assumes.NotNull(this.rpcMarshaledContextTracker); // This should have been set in the Rpc property setter.
            return this.rpcMarshaledContextTracker;
        }
    }

    /// <summary>
    /// Sets the <see cref="MessagePackSerializerOptions"/> to use for serialization of user data.
    /// </summary>
    /// <param name="options">
    /// The options to use. Before this call, the options used come from <see cref="DefaultUserDataSerializationOptions"/>.
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
        this.deserializationToStringHelper.Activate(contentBuffer, this.messageSerializationOptions);
        try
        {
            tracingCallbacks?.OnMessageDeserialized(message, this.deserializationToStringHelper);
        }
        finally
        {
            this.deserializationToStringHelper.Deactivate();
        }

        return message;
    }

    /// <inheritdoc/>
    public void Serialize(IBufferWriter<byte> contentBuffer, JsonRpcMessage message)
    {
        if (message is Protocol.JsonRpcRequest request && request.Arguments is not null && request.ArgumentsList is null && !(request.Arguments is IReadOnlyDictionary<string, object?>))
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
        try
        {
            this.messageSerializationOptions.Resolver.GetFormatterWithVerify<JsonRpcMessage>().Serialize(ref writer, message, this.messageSerializationOptions);
            writer.Flush();
        }
        catch (Exception ex)
        {
            throw new MessagePackSerializationException(string.Format(System.Globalization.CultureInfo.CurrentCulture, Resources.ErrorWritingJsonRpcMessage, ex.GetType().Name, ex.Message), ex);
        }
    }

    /// <inheritdoc/>
    public object GetJsonText(JsonRpcMessage message) => message is IJsonRpcMessagePackRetention retainedMsgPack ? MessagePackSerializer.ConvertToJson(retainedMsgPack.OriginalMessagePack, this.messageSerializationOptions) : throw new NotSupportedException();

    /// <inheritdoc/>
    Protocol.JsonRpcRequest IJsonRpcMessageFactory.CreateRequestMessage() => new OutboundJsonRpcRequest(this);

    /// <inheritdoc/>
    Protocol.JsonRpcError IJsonRpcMessageFactory.CreateErrorMessage() => new JsonRpcError(this.userDataSerializationOptions);

    /// <inheritdoc/>
    Protocol.JsonRpcResult IJsonRpcMessageFactory.CreateResultMessage() => new JsonRpcResult(this.messageSerializationOptions);

    void IJsonRpcFormatterTracingCallbacks.OnSerializationComplete(JsonRpcMessage message, ReadOnlySequence<byte> encodedMessage)
    {
        IJsonRpcTracingCallbacks? tracingCallbacks = this.rpc;
        this.serializationToStringHelper.Activate(encodedMessage, this.messageSerializationOptions);
        try
        {
            tracingCallbacks?.OnMessageSerialized(message, this.serializationToStringHelper);
        }
        finally
        {
            this.serializationToStringHelper.Deactivate();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes managed and native resources held by this instance.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> if being disposed; <see langword="false"/> if being finalized.</param>
    protected virtual void Dispose(bool disposing)
    {
        this.duplexPipeTracker?.Dispose();
    }

    /// <summary>
    /// Extracts a dictionary of property names and values from the specified params object.
    /// </summary>
    /// <param name="paramsObject">The params object.</param>
    /// <returns>A dictionary of argument values and another of declared argument types, or <see langword="null"/> if <paramref name="paramsObject"/> is null.</returns>
    /// <remarks>
    /// This method supports DataContractSerializer-compliant types. This includes C# anonymous types.
    /// </remarks>
    [return: NotNullIfNotNull("paramsObject")]
    private static (IReadOnlyDictionary<string, object?> ArgumentValues, IReadOnlyDictionary<string, Type> ArgumentTypes)? GetParamsObjectDictionary(object? paramsObject)
    {
        if (paramsObject is null)
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
        bool isDataContract = paramsTypeInfo.GetCustomAttribute<DataContractAttribute>() is not null;

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
                DataMemberAttribute? dataMemberAttribute = memberInfo.GetCustomAttribute<DataMemberAttribute>();
                if (dataMemberAttribute is null)
                {
                    return false;
                }

                if (!dataMemberAttribute.EmitDefaultValue)
                {
                    throw new NotSupportedException($"(DataMemberAttribute.EmitDefaultValue == false) is not supported but was found on: {memberInfo.DeclaringType!.FullName}.{memberInfo.Name}.");
                }

                key = dataMemberAttribute.Name ?? memberInfo.Name;
                return true;
            }
            else
            {
                return memberInfo.GetCustomAttribute<IgnoreDataMemberAttribute>() is null;
            }
        }

        foreach (PropertyInfo property in paramsTypeInfo.GetProperties(bindingFlags))
        {
            if (property.GetMethod is not null)
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
    /// Reads a string with an optimized path for the value "2.0".
    /// </summary>
    /// <param name="reader">The reader to use.</param>
    /// <returns>The decoded string.</returns>
    private static unsafe string ReadProtocolVersion(ref MessagePackReader reader)
    {
        // Recognize "2.0" since we expect it and can avoid decoding and allocating a new string for it.
        ReadOnlySpan<byte> valueBytes = MessagePack.Internal.CodeGenHelpers.ReadStringSpan(ref reader);
        if (Version2.TryRead(valueBytes))
        {
            return Version2.Value;
        }
        else
        {
            // It wasn't the expected value, so decode it.
            fixed (byte* pValueBytes = valueBytes)
            {
                return Encoding.UTF8.GetString(pValueBytes, valueBytes.Length);
            }
        }
    }

    /// <summary>
    /// Writes the JSON-RPC version property name and value in a highly optimized way.
    /// </summary>
    private static void WriteProtocolVersionPropertyAndValue(ref MessagePackWriter writer, string version)
    {
        VersionPropertyName.Write(ref writer);
        if (!Version2.TryWrite(ref writer, version))
        {
            writer.Write(version);
        }
    }

    private static void ReadUnknownProperty(ref MessagePackReader reader, ref Dictionary<string, ReadOnlySequence<byte>>? topLevelProperties, ReadOnlySpan<byte> stringKey)
    {
        topLevelProperties ??= new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.Ordinal);
#if NETSTANDARD2_1_OR_GREATER
        string name = Encoding.UTF8.GetString(stringKey);
#else
        string name = Encoding.UTF8.GetString(stringKey.ToArray());
#endif
        topLevelProperties.Add(name, GetSliceForNextToken(ref reader));
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

        // Add our own resolvers to fill in specialized behavior if the user doesn't provide/override it by their own resolver.
        var resolvers = new IFormatterResolver[]
        {
            // Support for marshalled objects.
            new RpcMarshalableResolver(this),

            userSuppliedOptions.Resolver,

            // Add stateless, non-specialized resolvers that help basic functionality to "just work".
            BasicTypesResolver,

            // Stateful or per-connection resolvers.
            this.progressFormatterResolver,
            this.asyncEnumerableFormatterResolver,
            this.pipeFormatterResolver,
            this.exceptionResolver,
        };

        // Wrap the resolver in another class as a way to pass information to our custom formatters.
        IFormatterResolver userDataResolver = new ResolverWrapper(CompositeResolver.Create(formatters, resolvers), this);

        return userSuppliedOptions
            .WithCompression(MessagePackCompression.None) // If/when we support LZ4 compression, it will be at the message level -- not the user-data level.
            .WithResolver(userDataResolver);
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
            new TraceParentFormatter(),
        };
        var resolvers = new IFormatterResolver[]
        {
            StandardResolverAllowPrivate.Instance,
        };
        return CompositeResolver.Create(formatters, resolvers);
    }

    [DebuggerDisplay("{" + nameof(Value) + "}")]
    private struct CommonString
    {
        internal CommonString(string value)
        {
            Requires.Argument(value.Length > 0 && value.Length <= 16, nameof(value), "Length must be >0 and <=16.");
            this.Value = value;
            ReadOnlyMemory<byte> encodedBytes = MessagePack.Internal.CodeGenHelpers.GetEncodedStringBytes(value);
            this.EncodedBytes = encodedBytes;

            ReadOnlySpan<byte> span = this.EncodedBytes.Span.Slice(1);
            this.Key = MessagePack.Internal.AutomataKeyGen.GetKey(ref span); // header is 1 byte because string length <= 16
            this.Key2 = span.Length > 0 ? (ulong?)MessagePack.Internal.AutomataKeyGen.GetKey(ref span) : null;
        }

        /// <summary>
        /// Gets the original string.
        /// </summary>
        internal string Value { get; }

        /// <summary>
        /// Gets the 64-bit integer that represents the string without decoding it.
        /// </summary>
        private ulong Key { get; }

        /// <summary>
        /// Gets the next 64-bit integer that represents the string without decoding it.
        /// </summary>
        private ulong? Key2 { get; }

        /// <summary>
        /// Gets the messagepack header and UTF-8 bytes for this string.
        /// </summary>
        private ReadOnlyMemory<byte> EncodedBytes { get; }

        /// <summary>
        /// Writes out the messagepack binary for this common string, if it matches the given value.
        /// </summary>
        /// <param name="writer">The writer to use.</param>
        /// <param name="value">The value to be written, if it matches this <see cref="CommonString"/>.</param>
        /// <returns><see langword="true"/> if <paramref name="value"/> matches this <see cref="Value"/> and it was written; <see langword="false"/> otherwise.</returns>
        internal bool TryWrite(ref MessagePackWriter writer, string value)
        {
            if (value == this.Value)
            {
                this.Write(ref writer);
                return true;
            }

            return false;
        }

        internal void Write(ref MessagePackWriter writer) => writer.WriteRaw(this.EncodedBytes.Span);

        /// <summary>
        /// Checks whether a span of UTF-8 bytes equal this common string.
        /// </summary>
        /// <param name="utf8String">The UTF-8 string.</param>
        /// <returns><see langword="true"/> if the UTF-8 bytes are the encoding of this common string; <see langword="false"/> otherwise.</returns>
        internal bool TryRead(ReadOnlySpan<byte> utf8String)
        {
            if (utf8String.Length != this.EncodedBytes.Length - 1)
            {
                return false;
            }

            ulong key1 = MessagePack.Internal.AutomataKeyGen.GetKey(ref utf8String);
            if (key1 != this.Key)
            {
                return false;
            }

            if (utf8String.Length > 0)
            {
                if (!this.Key2.HasValue)
                {
                    return false;
                }

                ulong key2 = MessagePack.Internal.AutomataKeyGen.GetKey(ref utf8String);
                if (key2 != this.Key2.Value)
                {
                    return false;
                }
            }
            else if (this.Key2.HasValue)
            {
                return false;
            }

            return true;
        }
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

        public override string ToString() => "<raw msgpack>";

        /// <summary>
        /// Reads one raw messagepack token.
        /// </summary>
        /// <param name="reader">The reader to use.</param>
        /// <param name="copy"><see langword="true"/> if the token must outlive the lifetime of the reader's underlying buffer; <see langword="false"/> otherwise.</param>
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

        internal object Deserialize(Type type, MessagePackSerializerOptions options)
        {
            return this.rawSequence.IsEmpty
                ? MessagePackSerializer.Deserialize(type, this.rawMemory, options)
                : MessagePackSerializer.Deserialize(type, this.rawSequence, options);
        }

        internal T Deserialize<T>(MessagePackSerializerOptions options)
        {
            return this.rawSequence.IsEmpty
                ? MessagePackSerializer.Deserialize<T>(this.rawMemory, options)
                : MessagePackSerializer.Deserialize<T>(this.rawSequence, options);
        }
    }

    private class ResolverWrapper : IFormatterResolver
    {
        private readonly IFormatterResolver inner;

        internal ResolverWrapper(IFormatterResolver inner, MessagePackFormatter formatter)
        {
            this.inner = inner;
            this.Formatter = formatter;
        }

        internal MessagePackFormatter Formatter { get; }

        public IMessagePackFormatter<T> GetFormatter<T>() => this.inner.GetFormatter<T>();
    }

    private class MessagePackFormatterConverter : IFormatterConverter
    {
        private readonly MessagePackSerializerOptions options;

        internal MessagePackFormatterConverter(MessagePackSerializerOptions options)
        {
            this.options = options;
        }

        public object Convert(object value, Type type) => ((RawMessagePack)value).Deserialize(type, this.options);

        public object Convert(object value, TypeCode typeCode)
        {
            return typeCode switch
            {
                TypeCode.Object => ((RawMessagePack)value).Deserialize<object>(this.options),
                _ => ExceptionSerializationHelpers.Convert(this, value, typeCode),
            };
        }

        public bool ToBoolean(object value) => ((RawMessagePack)value).Deserialize<bool>(this.options);

        public byte ToByte(object value) => ((RawMessagePack)value).Deserialize<byte>(this.options);

        public char ToChar(object value) => ((RawMessagePack)value).Deserialize<char>(this.options);

        public DateTime ToDateTime(object value) => ((RawMessagePack)value).Deserialize<DateTime>(this.options);

        public decimal ToDecimal(object value) => ((RawMessagePack)value).Deserialize<decimal>(this.options);

        public double ToDouble(object value) => ((RawMessagePack)value).Deserialize<double>(this.options);

        public short ToInt16(object value) => ((RawMessagePack)value).Deserialize<short>(this.options);

        public int ToInt32(object value) => ((RawMessagePack)value).Deserialize<int>(this.options);

        public long ToInt64(object value) => ((RawMessagePack)value).Deserialize<long>(this.options);

        public sbyte ToSByte(object value) => ((RawMessagePack)value).Deserialize<sbyte>(this.options);

        public float ToSingle(object value) => ((RawMessagePack)value).Deserialize<float>(this.options);

        public string? ToString(object value) => value is null ? null : ((RawMessagePack)value).Deserialize<string?>(this.options);

        public ushort ToUInt16(object value) => ((RawMessagePack)value).Deserialize<ushort>(this.options);

        public uint ToUInt32(object value) => ((RawMessagePack)value).Deserialize<uint>(this.options);

        public ulong ToUInt64(object value) => ((RawMessagePack)value).Deserialize<ulong>(this.options);
    }

    /// <summary>
    /// A recyclable object that can serialize a message to JSON on demand.
    /// </summary>
    /// <remarks>
    /// In perf traces, creation of this object used to show up as one of the most allocated objects.
    /// It is used even when tracing isn't active. So we changed its design it to be reused,
    /// since its lifetime is only required during a synchronous call to a trace API.
    /// </remarks>
    private class ToStringHelper
    {
        private ReadOnlySequence<byte>? encodedMessage;
        private MessagePackSerializerOptions? options;
        private string? jsonString;

        public override string ToString()
        {
            Verify.Operation(this.encodedMessage.HasValue, "This object has not been activated. It may have already been recycled.");

            return this.jsonString ??= MessagePackSerializer.ConvertToJson(this.encodedMessage.Value, this.options);
        }

        /// <summary>
        /// Initializes this object to represent a message.
        /// </summary>
        internal void Activate(ReadOnlySequence<byte> encodedMessage, MessagePackSerializerOptions options)
        {
            this.encodedMessage = encodedMessage;
            this.options = options;
        }

        /// <summary>
        /// Cleans out this object to release memory and ensure <see cref="ToString"/> throws if someone uses it after deactivation.
        /// </summary>
        internal void Deactivate()
        {
            this.encodedMessage = null;
            this.options = null;
            this.jsonString = null;
        }
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
                bool clientRequiresNamedArgs = this.formatter.deserializingMessage is JsonRpcRequest { ApplicableMethodAttribute: { ClientRequiresNamedArguments: true } };
                return (TClass)this.formatter.FormatterProgressTracker.CreateProgress(this.formatter.rpc, token, typeof(TClass), clientRequiresNamedArgs);
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
#pragma warning disable CA1812
        private class PreciseTypeFormatter<T> : IMessagePackFormatter<IAsyncEnumerable<T>?>
#pragma warning restore CA1812
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
                        default:
                            reader.Skip();
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
#pragma warning disable CA1812
        private class GeneratorFormatter<TClass, TElement> : IMessagePackFormatter<TClass>
            where TClass : IAsyncEnumerable<TElement>
#pragma warning restore CA1812
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
                        formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(DuplexPipeFormatter<>).MakeGenericType(typeof(T)), this.mainFormatter)!;
                    }
                    else if (typeof(PipeReader).IsAssignableFrom(typeof(T)))
                    {
                        formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(PipeReaderFormatter<>).MakeGenericType(typeof(T)), this.mainFormatter)!;
                    }
                    else if (typeof(PipeWriter).IsAssignableFrom(typeof(T)))
                    {
                        formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(PipeWriterFormatter<>).MakeGenericType(typeof(T)), this.mainFormatter)!;
                    }
                    else if (typeof(Stream).IsAssignableFrom(typeof(T)))
                    {
                        formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(StreamFormatter<>).MakeGenericType(typeof(T)), this.mainFormatter)!;
                    }

                    this.pipeFormatters.Add(typeof(T), formatter);
                }

                return (IMessagePackFormatter<T>?)formatter;
            }
        }

#pragma warning disable CA1812
        private class DuplexPipeFormatter<T> : IMessagePackFormatter<T?>
            where T : class, IDuplexPipe
#pragma warning restore CA1812
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

                return (T)this.formatter.DuplexPipeTracker.GetPipe(reader.ReadUInt64());
            }

            public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
            {
                if (this.formatter.DuplexPipeTracker.GetULongToken(value) is { } token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }
        }

#pragma warning disable CA1812
        private class PipeReaderFormatter<T> : IMessagePackFormatter<T?>
            where T : PipeReader
#pragma warning restore CA1812
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

                return (T)this.formatter.DuplexPipeTracker.GetPipeReader(reader.ReadUInt64());
            }

            public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
            {
                if (this.formatter.DuplexPipeTracker.GetULongToken(value) is { } token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }
        }

#pragma warning disable CA1812
        private class PipeWriterFormatter<T> : IMessagePackFormatter<T?>
            where T : PipeWriter
#pragma warning restore CA1812
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

                return (T)this.formatter.DuplexPipeTracker.GetPipeWriter(reader.ReadUInt64());
            }

            public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
            {
                if (this.formatter.DuplexPipeTracker.GetULongToken(value) is { } token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }
        }

#pragma warning disable CA1812
        private class StreamFormatter<T> : IMessagePackFormatter<T?>
            where T : Stream
#pragma warning restore CA1812
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

                return (T)this.formatter.DuplexPipeTracker.GetPipe(reader.ReadUInt64()).AsStream();
            }

            public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
            {
                if (this.formatter.DuplexPipeTracker.GetULongToken(value?.UsePipe()) is { } token)
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

    private class RpcMarshalableResolver : IFormatterResolver
    {
        private readonly MessagePackFormatter formatter;
        private readonly Dictionary<Type, object> formatters = new Dictionary<Type, object>();

        internal RpcMarshalableResolver(MessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            if (typeof(T).IsValueType)
            {
                return null;
            }

            lock (this.formatters)
            {
                if (this.formatters.TryGetValue(typeof(T), out object? cachedFormatter))
                {
                    return (IMessagePackFormatter<T>)cachedFormatter;
                }
            }

            if (MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(typeof(T), out JsonRpcProxyOptions? proxyOptions, out JsonRpcTargetOptions? targetOptions))
            {
                object formatter = Activator.CreateInstance(
                    typeof(RpcMarshalableFormatter<>).MakeGenericType(typeof(T)),
                    this.formatter,
                    proxyOptions,
                    targetOptions)!;

                lock (this.formatters)
                {
                    if (!this.formatters.TryGetValue(typeof(T), out object? cachedFormatter))
                    {
                        this.formatters.Add(typeof(T), cachedFormatter = formatter);
                    }

                    return (IMessagePackFormatter<T>)cachedFormatter;
                }
            }

            return null;
        }
    }

#pragma warning disable CA1812
    private class RpcMarshalableFormatter<T> : IMessagePackFormatter<T?>
        where T : class
#pragma warning restore CA1812
    {
        private MessagePackFormatter messagePackFormatter;
        private JsonRpcProxyOptions proxyOptions;
        private JsonRpcTargetOptions targetOptions;

        public RpcMarshalableFormatter(MessagePackFormatter messagePackFormatter, JsonRpcProxyOptions proxyOptions, JsonRpcTargetOptions targetOptions)
        {
            this.messagePackFormatter = messagePackFormatter;
            this.proxyOptions = proxyOptions;
            this.targetOptions = targetOptions;
        }

        public T? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            MessageFormatterRpcMarshaledContextTracker.MarshalToken? token = MessagePackSerializer.Deserialize<MessageFormatterRpcMarshaledContextTracker.MarshalToken?>(ref reader, options);
            return token.HasValue ? (T?)this.messagePackFormatter.RpcMarshaledContextTracker.GetObject(typeof(T), token, this.proxyOptions) : null;
        }

        public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNil();
            }
            else
            {
                MessageFormatterRpcMarshaledContextTracker.MarshalToken token = this.messagePackFormatter.RpcMarshaledContextTracker.GetToken(value, this.targetOptions, typeof(T));
                MessagePackSerializer.Serialize(ref writer, token, options);
            }
        }
    }

    /// <summary>
    /// Manages serialization of any <see cref="Exception"/>-derived type that follows standard <see cref="SerializableAttribute"/> rules.
    /// </summary>
    /// <remarks>
    /// A serializable class will:
    /// 1. Derive from <see cref="Exception"/>
    /// 2. Be attributed with <see cref="SerializableAttribute"/>
    /// 3. Declare a constructor with a signature of (<see cref="SerializationInfo"/>, <see cref="StreamingContext"/>).
    /// </remarks>
    private class MessagePackExceptionResolver : IFormatterResolver
    {
        /// <summary>
        /// Tracks recursion count while serializing or deserializing an exception.
        /// </summary>
        /// <devremarks>
        /// This is placed here (<em>outside</em> the generic <see cref="ExceptionFormatter{T}"/> class)
        /// so that it's one counter shared across all exception types that may be serialized or deserialized.
        /// </devremarks>
        private static ThreadLocal<int> exceptionRecursionCounter = new();

        private readonly object[] formatterActivationArgs;

        private readonly Dictionary<Type, object?> formatterCache = new Dictionary<Type, object?>();

        internal MessagePackExceptionResolver(MessagePackFormatter formatter)
        {
            this.formatterActivationArgs = new object[] { formatter };
        }

        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            lock (this.formatterCache)
            {
                if (this.formatterCache.TryGetValue(typeof(T), out object? cachedFormatter))
                {
                    return (IMessagePackFormatter<T>?)cachedFormatter;
                }

                IMessagePackFormatter<T>? formatter = null;
                if (typeof(Exception).IsAssignableFrom(typeof(T)) && typeof(T).GetCustomAttribute<SerializableAttribute>() is object)
                {
                    formatter = (IMessagePackFormatter<T>)Activator.CreateInstance(typeof(ExceptionFormatter<>).MakeGenericType(typeof(T)), this.formatterActivationArgs)!;
                }

                this.formatterCache.Add(typeof(T), formatter);
                return formatter;
            }
        }

#pragma warning disable CS8766 // Nullability of reference types in return type doesn't match implicitly implemented member (possibly because of nullability attributes).
#pragma warning disable CA1812
        private class ExceptionFormatter<T> : IMessagePackFormatter<T>
            where T : Exception
#pragma warning restore CA1812
        {
            private readonly MessagePackFormatter formatter;

            public ExceptionFormatter(MessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            [return: MaybeNull]
            public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                Assumes.NotNull(this.formatter.rpc);
                if (reader.TryReadNil())
                {
                    return null;
                }

                // We have to guard our own recursion because the serializer has no visibility into inner exceptions.
                // Each exception in the russian doll is a new serialization job from its perspective.
                exceptionRecursionCounter.Value++;
                try
                {
                    if (exceptionRecursionCounter.Value > this.formatter.rpc.ExceptionOptions.RecursionLimit)
                    {
                        // Exception recursion has gone too deep. Skip this value and return null as if there were no inner exception.
                        // Note that in skipping, the parser may use recursion internally and may still throw if its own limits are exceeded.
                        reader.Skip();
                        return null;
                    }

                    var info = new SerializationInfo(typeof(T), new MessagePackFormatterConverter(options));
                    int memberCount = reader.ReadMapHeader();
                    for (int i = 0; i < memberCount; i++)
                    {
                        string name = reader.ReadString();

                        // SerializationInfo.GetValue(string, typeof(object)) does not call our formatter,
                        // so the caller will get a boxed RawMessagePack struct in that case.
                        // Although we can't do much about *that* in general, we can at least ensure that null values
                        // are represented as null instead of this boxed struct.
                        var value = reader.TryReadNil() ? null : (object)RawMessagePack.ReadRaw(ref reader, false);

                        info.AddSafeValue(name, value);
                    }

                    var resolverWrapper = options.Resolver as ResolverWrapper;
                    Report.If(resolverWrapper is null, "Unexpected resolver type.");
                    return ExceptionSerializationHelpers.Deserialize<T>(this.formatter.rpc, info, resolverWrapper?.Formatter.rpc?.TraceSource);
                }
                finally
                {
                    exceptionRecursionCounter.Value--;
                }
            }

            public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
            {
                if (value is null)
                {
                    writer.WriteNil();
                    return;
                }

                exceptionRecursionCounter.Value++;
                try
                {
                    if (exceptionRecursionCounter.Value > this.formatter.rpc?.ExceptionOptions.RecursionLimit)
                    {
                        // Exception recursion has gone too deep. Skip this value and write null as if there were no inner exception.
                        writer.WriteNil();
                        return;
                    }

                    var info = new SerializationInfo(typeof(T), new MessagePackFormatterConverter(options));
                    ExceptionSerializationHelpers.Serialize(value, info);
                    writer.WriteMapHeader(info.GetSafeMemberCount());
                    foreach (SerializationEntry element in info.GetSafeMembers())
                    {
                        writer.Write(element.Name);
                        MessagePackSerializer.Serialize(element.ObjectType, ref writer, element.Value, options);
                    }
                }
                finally
                {
                    exceptionRecursionCounter.Value--;
                }
            }
        }
#pragma warning restore
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
                // We read the property name in this fancy way in order to avoid paying to decode and allocate a string when we already know what we're looking for.
                ReadOnlySpan<byte> stringKey = MessagePack.Internal.CodeGenHelpers.ReadStringSpan(ref readAhead);
                if (MethodPropertyName.TryRead(stringKey))
                {
                    return options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcRequest>().Deserialize(ref reader, options);
                }
                else if (ResultPropertyName.TryRead(stringKey))
                {
                    return options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcResult>().Deserialize(ref reader, options);
                }
                else if (ErrorPropertyName.TryRead(stringKey))
                {
                    return options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError>().Deserialize(ref reader, options);
                }
                else
                {
                    // Skip over the value of this property.
                    readAhead.Skip();
                }
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
            Dictionary<string, ReadOnlySequence<byte>>? topLevelProperties = null;
            for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
            {
                // We read the property name in this fancy way in order to avoid paying to decode and allocate a string when we already know what we're looking for.
                ReadOnlySpan<byte> stringKey = MessagePack.Internal.CodeGenHelpers.ReadStringSpan(ref reader);
                if (VersionPropertyName.TryRead(stringKey))
                {
                    result.Version = ReadProtocolVersion(ref reader);
                }
                else if (IdPropertyName.TryRead(stringKey))
                {
                    result.RequestId = options.Resolver.GetFormatterWithVerify<RequestId>().Deserialize(ref reader, options);
                }
                else if (MethodPropertyName.TryRead(stringKey))
                {
                    result.Method = reader.ReadString();
                }
                else if (ParamsPropertyName.TryRead(stringKey))
                {
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
                }
                else if (TraceParentPropertyName.TryRead(stringKey))
                {
                    TraceParent traceParent = options.Resolver.GetFormatterWithVerify<TraceParent>().Deserialize(ref reader, options);
                    result.TraceParent = traceParent.ToString();
                }
                else if (TraceStatePropertyName.TryRead(stringKey))
                {
                    result.TraceState = ReadTraceState(ref reader);
                }
                else
                {
                    ReadUnknownProperty(ref reader, ref topLevelProperties, stringKey);
                }
            }

            if (topLevelProperties is not null)
            {
                result.TopLevelPropertyBag = new TopLevelPropertyBag(this.formatter.userDataSerializationOptions, topLevelProperties);
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
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
                {
                    this.formatter.rpc?.TraceSource.TraceData(TraceEventType.Error, (int)JsonRpc.TraceEvents.ProgressNotificationError, ex);
                }
            }

            reader.Depth--;
            return result;
        }

        public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcRequest value, MessagePackSerializerOptions options)
        {
            var topLevelPropertyBagMessage = value as IMessageWithTopLevelPropertyBag;

            int mapElementCount = value.RequestId.IsEmpty ? 3 : 4;
            if (value.TraceParent?.Length > 0)
            {
                mapElementCount++;
                if (value.TraceState?.Length > 0)
                {
                    mapElementCount++;
                }
            }

            mapElementCount += topLevelPropertyBagMessage?.TopLevelPropertyBag?.PropertyCount ?? 0;
            writer.WriteMapHeader(mapElementCount);

            WriteProtocolVersionPropertyAndValue(ref writer, value.Version);

            if (!value.RequestId.IsEmpty)
            {
                IdPropertyName.Write(ref writer);
                options.Resolver.GetFormatterWithVerify<RequestId>().Serialize(ref writer, value.RequestId, options);
            }

            MethodPropertyName.Write(ref writer);
            writer.Write(value.Method);

            ParamsPropertyName.Write(ref writer);
            if (value.ArgumentsList is not null)
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
            else if (value.NamedArguments is not null)
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

            if (value.TraceParent?.Length > 0)
            {
                TraceParentPropertyName.Write(ref writer);
                options.Resolver.GetFormatterWithVerify<TraceParent>().Serialize(ref writer, new TraceParent(value.TraceParent), options);

                if (value.TraceState?.Length > 0)
                {
                    TraceStatePropertyName.Write(ref writer);
                    WriteTraceState(ref writer, value.TraceState);
                }
            }

            topLevelPropertyBagMessage?.TopLevelPropertyBag?.WritePropertiesAndClear(ref writer);
        }

        private static void WriteTraceState(ref MessagePackWriter writer, string traceState)
        {
            ReadOnlySpan<char> traceStateChars = traceState.AsSpan();

            // Count elements first so we can write the header.
            int elementCount = 1;
            int commaIndex;
            while ((commaIndex = traceStateChars.IndexOf(',')) >= 0)
            {
                elementCount++;
                traceStateChars = traceStateChars.Slice(commaIndex + 1);
            }

            // For every element, we have a key and value to record.
            writer.WriteArrayHeader(elementCount * 2);

            traceStateChars = traceState.AsSpan();
            while ((commaIndex = traceStateChars.IndexOf(',')) >= 0)
            {
                ReadOnlySpan<char> element = traceStateChars.Slice(0, commaIndex);
                WritePair(ref writer, element);
                traceStateChars = traceStateChars.Slice(commaIndex + 1);
            }

            // Write out the last one.
            WritePair(ref writer, traceStateChars);

            static void WritePair(ref MessagePackWriter writer, ReadOnlySpan<char> pair)
            {
                int equalsIndex = pair.IndexOf('=');
                ReadOnlySpan<char> key = pair.Slice(0, equalsIndex);
                ReadOnlySpan<char> value = pair.Slice(equalsIndex + 1);
                writer.Write(key);
                writer.Write(value);
            }
        }

        private static unsafe string ReadTraceState(ref MessagePackReader reader)
        {
            int elements = reader.ReadArrayHeader();
            if (elements % 2 != 0)
            {
                throw new NotSupportedException("Odd number of elements not expected.");
            }

            // With care, we could probably assemble this string with just two allocations (the string + a char[]).
            var resultBuilder = new StringBuilder();
            for (int i = 0; i < elements; i += 2)
            {
                if (resultBuilder.Length > 0)
                {
                    resultBuilder.Append(',');
                }

                resultBuilder.Append(reader.ReadString());
                resultBuilder.Append('=');
                resultBuilder.Append(reader.ReadString());
            }

            return resultBuilder.ToString();
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

            Dictionary<string, ReadOnlySequence<byte>>? topLevelProperties = null;
            options.Security.DepthStep(ref reader);
            int propertyCount = reader.ReadMapHeader();
            for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
            {
                // We read the property name in this fancy way in order to avoid paying to decode and allocate a string when we already know what we're looking for.
                ReadOnlySpan<byte> stringKey = MessagePack.Internal.CodeGenHelpers.ReadStringSpan(ref reader);
                if (VersionPropertyName.TryRead(stringKey))
                {
                    result.Version = ReadProtocolVersion(ref reader);
                }
                else if (IdPropertyName.TryRead(stringKey))
                {
                    result.RequestId = options.Resolver.GetFormatterWithVerify<RequestId>().Deserialize(ref reader, options);
                }
                else if (ResultPropertyName.TryRead(stringKey))
                {
                    result.MsgPackResult = GetSliceForNextToken(ref reader);
                }
                else
                {
                    ReadUnknownProperty(ref reader, ref topLevelProperties, stringKey);
                }
            }

            if (topLevelProperties is not null)
            {
                result.TopLevelPropertyBag = new TopLevelPropertyBag(this.formatter.userDataSerializationOptions, topLevelProperties);
            }

            reader.Depth--;
            return result;
        }

        public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcResult value, MessagePackSerializerOptions options)
        {
            var topLevelPropertyBagMessage = value as IMessageWithTopLevelPropertyBag;

            int mapElementCount = 3;
            mapElementCount += topLevelPropertyBagMessage?.TopLevelPropertyBag?.PropertyCount ?? 0;
            writer.WriteMapHeader(mapElementCount);

            WriteProtocolVersionPropertyAndValue(ref writer, value.Version);

            IdPropertyName.Write(ref writer);
            options.Resolver.GetFormatterWithVerify<RequestId>().Serialize(ref writer, value.RequestId, options);

            ResultPropertyName.Write(ref writer);
            if (value.ResultDeclaredType is object && value.ResultDeclaredType != typeof(void))
            {
                MessagePackSerializer.Serialize(value.ResultDeclaredType, ref writer, value.Result, this.formatter.userDataSerializationOptions);
            }
            else
            {
                DynamicObjectTypeFallbackFormatter.Instance.Serialize(ref writer, value.Result, this.formatter.userDataSerializationOptions);
            }

            topLevelPropertyBagMessage?.TopLevelPropertyBag?.WritePropertiesAndClear(ref writer);
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
            var error = new JsonRpcError(this.formatter.messageSerializationOptions)
            {
                OriginalMessagePack = reader.Sequence,
            };

            Dictionary<string, ReadOnlySequence<byte>>? topLevelProperties = null;
            options.Security.DepthStep(ref reader);
            int propertyCount = reader.ReadMapHeader();
            for (int propertyIdx = 0; propertyIdx < propertyCount; propertyIdx++)
            {
                // We read the property name in this fancy way in order to avoid paying to decode and allocate a string when we already know what we're looking for.
                ReadOnlySpan<byte> stringKey = MessagePack.Internal.CodeGenHelpers.ReadStringSpan(ref reader);
                if (VersionPropertyName.TryRead(stringKey))
                {
                    error.Version = ReadProtocolVersion(ref reader);
                }
                else if (IdPropertyName.TryRead(stringKey))
                {
                    error.RequestId = options.Resolver.GetFormatterWithVerify<RequestId>().Deserialize(ref reader, options);
                }
                else if (ErrorPropertyName.TryRead(stringKey))
                {
                    error.Error = options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError.ErrorDetail?>().Deserialize(ref reader, options);
                }
                else
                {
                    ReadUnknownProperty(ref reader, ref topLevelProperties, stringKey);
                }
            }

            if (topLevelProperties is not null)
            {
                error.TopLevelPropertyBag = new TopLevelPropertyBag(this.formatter.userDataSerializationOptions, topLevelProperties);
            }

            reader.Depth--;
            return error;
        }

        public void Serialize(ref MessagePackWriter writer, Protocol.JsonRpcError value, MessagePackSerializerOptions options)
        {
            var topLevelPropertyBagMessage = value as IMessageWithTopLevelPropertyBag;

            int mapElementCount = 3;
            mapElementCount += topLevelPropertyBagMessage?.TopLevelPropertyBag?.PropertyCount ?? 0;
            writer.WriteMapHeader(mapElementCount);

            WriteProtocolVersionPropertyAndValue(ref writer, value.Version);

            IdPropertyName.Write(ref writer);
            options.Resolver.GetFormatterWithVerify<RequestId>().Serialize(ref writer, value.RequestId, options);

            ErrorPropertyName.Write(ref writer);
            options.Resolver.GetFormatterWithVerify<Protocol.JsonRpcError.ErrorDetail?>().Serialize(ref writer, value.Error, options);

            topLevelPropertyBagMessage?.TopLevelPropertyBag?.WritePropertiesAndClear(ref writer);
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
                    default:
                        reader.Skip();
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

    /// <summary>
    /// Enables formatting the default/empty <see cref="EventArgs"/> class.
    /// </summary>
    private class EventArgsFormatter : IMessagePackFormatter<EventArgs>
    {
        /// <summary>
        /// The singleton instance of the formatter to be used.
        /// </summary>
        internal static readonly IMessagePackFormatter<EventArgs> Instance = new EventArgsFormatter();

        private EventArgsFormatter()
        {
        }

        /// <inheritdoc/>
        public void Serialize(ref MessagePackWriter writer, EventArgs value, MessagePackSerializerOptions options)
        {
            writer.WriteMapHeader(0);
        }

        /// <inheritdoc/>
        public EventArgs Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            reader.Skip();
            return EventArgs.Empty;
        }
    }

    private class TraceParentFormatter : IMessagePackFormatter<TraceParent>
    {
        public unsafe TraceParent Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.ReadArrayHeader() != 2)
            {
                throw new NotSupportedException("Unexpected array length.");
            }

            var result = default(TraceParent);
            result.Version = reader.ReadByte();
            if (result.Version != 0)
            {
                throw new NotSupportedException("traceparent version " + result.Version + " is not supported.");
            }

            if (reader.ReadArrayHeader() != 3)
            {
                throw new NotSupportedException("Unexpected array length in version-format.");
            }

            ReadOnlySequence<byte> bytes = reader.ReadBytes() ?? throw new NotSupportedException("Expected traceid not found.");
            bytes.CopyTo(new Span<byte>(result.TraceId, TraceParent.TraceIdByteCount));

            bytes = reader.ReadBytes() ?? throw new NotSupportedException("Expected parentid not found.");
            bytes.CopyTo(new Span<byte>(result.ParentId, TraceParent.ParentIdByteCount));

            result.Flags = (TraceParent.TraceFlags)reader.ReadByte();

            return result;
        }

        public unsafe void Serialize(ref MessagePackWriter writer, TraceParent value, MessagePackSerializerOptions options)
        {
            if (value.Version != 0)
            {
                throw new NotSupportedException("traceparent version " + value.Version + " is not supported.");
            }

            writer.WriteArrayHeader(2);

            writer.Write(value.Version);

            writer.WriteArrayHeader(3);
            writer.Write(new ReadOnlySpan<byte>(value.TraceId, TraceParent.TraceIdByteCount));
            writer.Write(new ReadOnlySpan<byte>(value.ParentId, TraceParent.ParentIdByteCount));
            writer.Write((byte)value.Flags);
        }
    }

    private class TopLevelPropertyBag
    {
        private readonly MessagePackSerializerOptions serializerOptions;
        private readonly IReadOnlyDictionary<string, ReadOnlySequence<byte>>? inboundUnknownProperties;
        private Dictionary<string, Sequence<byte>>? outboundUnknownProperties;
        private bool outboundPropertiesAlreadyWritten;

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for an incoming message.
        /// </summary>
        /// <param name="userDataSerializationOptions">The serializer options to use for this data.</param>
        /// <param name="inboundUnknownProperties">The map of unrecognized inbound properties.</param>
        internal TopLevelPropertyBag(MessagePackSerializerOptions userDataSerializationOptions, IReadOnlyDictionary<string, ReadOnlySequence<byte>> inboundUnknownProperties)
        {
            this.serializerOptions = userDataSerializationOptions;
            this.inboundUnknownProperties = inboundUnknownProperties;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for an outbound message.
        /// </summary>
        /// <param name="serializerOptions">The serializer options to use for this data.</param>
        internal TopLevelPropertyBag(MessagePackSerializerOptions serializerOptions)
        {
            this.serializerOptions = serializerOptions;
            this.outboundUnknownProperties = new Dictionary<string, Sequence<byte>>();
        }

        internal int PropertyCount => this.outboundUnknownProperties?.Count ?? this.inboundUnknownProperties?.Count ?? 0;

        /// <summary>
        /// Writes the properties tracked by this collection to a messagepack writer.
        /// </summary>
        /// <param name="writer">The writer to use.</param>
        internal void WritePropertiesAndClear(ref MessagePackWriter writer)
        {
            if (this.outboundUnknownProperties is null)
            {
                throw new InvalidOperationException(Resources.OutboundMessageOnly);
            }

            Verify.Operation(!this.outboundPropertiesAlreadyWritten, Resources.UsableOnceOnly);

            foreach (KeyValuePair<string, Sequence<byte>> entry in this.outboundUnknownProperties)
            {
                writer.Write(entry.Key);
                writer.WriteRaw(entry.Value);
                entry.Value.Reset();
            }

            this.outboundUnknownProperties.Clear();

            // Throw if this method is called again, since recycling memory here means the operation cannot be repeated.
            this.outboundPropertiesAlreadyWritten = true;
        }

        internal bool TryGetTopLevelProperty<T>(string name, [MaybeNull] out T value)
        {
            if (this.inboundUnknownProperties is null)
            {
                throw new InvalidOperationException(Resources.InboundMessageOnly);
            }

            value = default;

            if (this.inboundUnknownProperties.TryGetValue(name, out ReadOnlySequence<byte> serializedValue) is true)
            {
                var reader = new MessagePackReader(serializedValue);
                value = MessagePackSerializer.Deserialize<T>(ref reader, this.serializerOptions);
                return true;
            }

            return false;
        }

        internal void SetTopLevelProperty<T>(string name, [MaybeNull] T value)
        {
            if (this.outboundUnknownProperties is null)
            {
                throw new InvalidOperationException(Resources.OutboundMessageOnly);
            }

            Sequence<byte> buffer = new();
            MessagePackWriter writer = new(buffer);
            MessagePackSerializer.Serialize(ref writer, value, this.serializerOptions);
            writer.Flush();
            this.outboundUnknownProperties[name] = buffer;
        }
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    [DataContract]
    private class OutboundJsonRpcRequest : Protocol.JsonRpcRequest, IMessageWithTopLevelPropertyBag
    {
        private readonly MessagePackFormatter formatter;

        internal OutboundJsonRpcRequest(MessagePackFormatter formatter)
        {
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        }

        public TopLevelPropertyBag? TopLevelPropertyBag { get; set; }

        public override bool TrySetTopLevelProperty<T>(string name, [MaybeNull] T value)
        {
            Requires.NotNullOrEmpty(name, nameof(name));
            Requires.Argument(!Constants.Request.IsPropertyReserved(name), nameof(name), Resources.ReservedPropertyName);

            this.TopLevelPropertyBag ??= new TopLevelPropertyBag(this.formatter.userDataSerializationOptions);
            this.TopLevelPropertyBag.SetTopLevelProperty(name, value);
            return true;
        }
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    [DataContract]
    private class JsonRpcRequest : Protocol.JsonRpcRequest, IJsonRpcMessageBufferManager, IJsonRpcMessagePackRetention, IMessageWithTopLevelPropertyBag
    {
        private readonly MessagePackFormatter formatter;

        internal JsonRpcRequest(MessagePackFormatter formatter)
        {
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        }

        public override int ArgumentCount => this.MsgPackNamedArguments?.Count ?? this.MsgPackPositionalArguments?.Count ?? base.ArgumentCount;

        public override IEnumerable<string>? ArgumentNames => this.MsgPackNamedArguments?.Keys;

        public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

        public TopLevelPropertyBag? TopLevelPropertyBag { get; set; }

        internal ReadOnlySequence<byte> MsgPackArguments { get; set; }

        internal IReadOnlyDictionary<string, ReadOnlySequence<byte>>? MsgPackNamedArguments { get; set; }

        internal IReadOnlyList<ReadOnlySequence<byte>>? MsgPackPositionalArguments { get; set; }

        internal JsonRpcMethodAttribute? ApplicableMethodAttribute { get; private set; }

        void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
        {
            Assumes.True(message == this);

            // Clear references to buffers that we are no longer entitled to.
            this.MsgPackNamedArguments = null;
            this.MsgPackPositionalArguments = null;
            this.TopLevelPropertyBag = null;
            this.MsgPackArguments = default;
            this.OriginalMessagePack = default;
        }

        public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, Span<object?> typedArguments)
        {
            // Consider the attribute applied to the particular overload that we're considering right now.
            this.ApplicableMethodAttribute = this.Method is not null ? this.formatter.rpc?.GetJsonRpcMethodAttribute(this.Method, parameters) : null;
            try
            {
                if (parameters.Length == 1 && this.MsgPackNamedArguments is not null)
                {
                    Assumes.NotNull(this.Method);

                    if (this.ApplicableMethodAttribute?.UseSingleObjectParameterDeserialization ?? false)
                    {
                        var reader = new MessagePackReader(this.MsgPackArguments);
                        try
                        {
                            // Deserialization of messages should never occur concurrently for a single instance of a formatter.
                            Assumes.True(this.formatter.deserializingMessageWithId.IsEmpty);
                            this.formatter.deserializingMessageWithId = this.RequestId;
                            this.formatter.deserializingMessage = this;
                            typedArguments[0] = MessagePackSerializer.Deserialize(parameters[0].ParameterType, ref reader, this.formatter.userDataSerializationOptions);
                            return ArgumentMatchResult.Success;
                        }
                        catch (MessagePackSerializationException)
                        {
                            return ArgumentMatchResult.ParameterArgumentTypeMismatch;
                        }
                        finally
                        {
                            this.formatter.deserializingMessageWithId = default;
                            this.formatter.deserializingMessage = null;
                        }
                    }
                }

                return base.TryGetTypedArguments(parameters, typedArguments);
            }
            finally
            {
                // Clear this, because we might choose another overload with a different attribute, and we don't want to 'leak' an attribute that isn't on the overload that is ultimately picked.
                this.ApplicableMethodAttribute = null;
            }
        }

        public override bool TryGetArgumentByNameOrIndex(string? name, int position, Type? typeHint, out object? value)
        {
            // If anyone asks us for an argument *after* we've been told deserialization is done, there's something very wrong.
            Assumes.True(this.MsgPackNamedArguments is not null || this.MsgPackPositionalArguments is not null);

            ReadOnlySequence<byte> msgpackArgument = default;
            if (position >= 0 && this.MsgPackPositionalArguments?.Count > position)
            {
                msgpackArgument = this.MsgPackPositionalArguments[position];
            }
            else if (name is object && this.MsgPackNamedArguments is not null)
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
                this.formatter.deserializingMessage = this;
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
                this.formatter.deserializingMessage = null;
            }
        }

        public override bool TryGetTopLevelProperty<T>(string name, [MaybeNull] out T value)
        {
            Requires.NotNullOrEmpty(name, nameof(name));
            Requires.Argument(!Constants.Request.IsPropertyReserved(name), nameof(name), Resources.ReservedPropertyName);

            value = default;
            return this.TopLevelPropertyBag is not null && this.TopLevelPropertyBag.TryGetTopLevelProperty(name, out value);
        }

        public override bool TrySetTopLevelProperty<T>(string name, [MaybeNull] T value)
        {
            Requires.NotNullOrEmpty(name, nameof(name));
            Requires.Argument(!Constants.Request.IsPropertyReserved(name), nameof(name), Resources.ReservedPropertyName);

            this.TopLevelPropertyBag ??= new TopLevelPropertyBag(this.formatter.userDataSerializationOptions);
            this.TopLevelPropertyBag.SetTopLevelProperty(name, value);
            return true;
        }
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    [DataContract]
    private class JsonRpcResult : Protocol.JsonRpcResult, IJsonRpcMessageBufferManager, IJsonRpcMessagePackRetention, IMessageWithTopLevelPropertyBag
    {
        private readonly MessagePackSerializerOptions serializerOptions;

        private Exception? resultDeserializationException;

        internal JsonRpcResult(MessagePackSerializerOptions serializerOptions)
        {
            this.serializerOptions = serializerOptions;
        }

        public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

        public TopLevelPropertyBag? TopLevelPropertyBag { get; set; }

        internal ReadOnlySequence<byte> MsgPackResult { get; set; }

        void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
        {
            Assumes.True(message == this);
            this.MsgPackResult = default;
            this.OriginalMessagePack = default;
        }

        public override T GetResult<T>()
        {
            if (this.resultDeserializationException is not null)
            {
                ExceptionDispatchInfo.Capture(this.resultDeserializationException).Throw();
            }

            return this.MsgPackResult.IsEmpty
                ? (T)this.Result!
                : MessagePackSerializer.Deserialize<T>(this.MsgPackResult, this.serializerOptions);
        }

        public override bool TryGetTopLevelProperty<T>(string name, [MaybeNull] out T value)
        {
            Requires.NotNullOrEmpty(name, nameof(name));
            Requires.Argument(!Constants.Result.IsPropertyReserved(name), nameof(name), Resources.ReservedPropertyName);

            value = default;
            return this.TopLevelPropertyBag is not null && this.TopLevelPropertyBag.TryGetTopLevelProperty(name, out value);
        }

        public override bool TrySetTopLevelProperty<T>(string name, [MaybeNull] T value)
        {
            Requires.NotNullOrEmpty(name, nameof(name));
            Requires.Argument(!Constants.Result.IsPropertyReserved(name), nameof(name), Resources.ReservedPropertyName);

            this.TopLevelPropertyBag ??= new TopLevelPropertyBag(this.serializerOptions);
            this.TopLevelPropertyBag.SetTopLevelProperty(name, value);
            return true;
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
    private class JsonRpcError : Protocol.JsonRpcError, IJsonRpcMessageBufferManager, IJsonRpcMessagePackRetention, IMessageWithTopLevelPropertyBag
    {
        private readonly MessagePackSerializerOptions serializerOptions;

        public JsonRpcError(MessagePackSerializerOptions serializerOptions)
        {
            this.serializerOptions = serializerOptions;
        }

        public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

        public TopLevelPropertyBag? TopLevelPropertyBag { get; set; }

        void IJsonRpcMessageBufferManager.DeserializationComplete(JsonRpcMessage message)
        {
            Assumes.True(message == this);
            if (this.Error is ErrorDetail privateDetail)
            {
                privateDetail.MsgPackData = default;
            }

            this.OriginalMessagePack = default;
        }

        public override bool TryGetTopLevelProperty<T>(string name, [MaybeNull] out T value)
        {
            Requires.NotNullOrEmpty(name, nameof(name));
            Requires.Argument(!Constants.Error.IsPropertyReserved(name), nameof(name), Resources.ReservedPropertyName);

            value = default;
            return this.TopLevelPropertyBag is not null && this.TopLevelPropertyBag.TryGetTopLevelProperty(name, out value);
        }

        public override bool TrySetTopLevelProperty<T>(string name, [MaybeNull] T value)
        {
            Requires.NotNullOrEmpty(name, nameof(name));
            Requires.Argument(!Constants.Error.IsPropertyReserved(name), nameof(name), Resources.ReservedPropertyName);

            this.TopLevelPropertyBag ??= new TopLevelPropertyBag(this.serializerOptions);
            this.TopLevelPropertyBag.SetTopLevelProperty(name, value);
            return true;
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

                // Clear the source now that we've deserialized to prevent GetData from attempting
                // deserialization later when the buffer may be recycled on another thread.
                this.MsgPackData = default;
            }
        }
    }
}
