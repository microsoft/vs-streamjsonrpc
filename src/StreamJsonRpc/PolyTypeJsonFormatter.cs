// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nerdbank.Streams;
using PolyType;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// A formatter that emits UTF-8 encoded JSON where user data should be serializable via the <see cref="JsonSerializer"/>.
/// This formatter is NativeAOT ready and relies on PolyType.
/// </summary>
[Experimental("PolyTypeJson")]
public partial class PolyTypeJsonFormatter : FormatterBase, IJsonRpcMessageFormatter, IJsonRpcMessageTextFormatter, IJsonRpcInstanceContainer, IJsonRpcMessageFactory, IJsonRpcFormatterTracingCallbacks
{
    private static readonly ProxyFactory ProxyFactory = ProxyFactory.NoDynamic;

    private static readonly JsonWriterOptions WriterOptions = new() { };

    private static readonly JsonDocumentOptions DocumentOptions = new() { };

    /// <summary>
    /// The <see cref="JsonSerializerOptions"/> to use for the envelope and built-in types.
    /// </summary>
    private static readonly JsonSerializerOptions BuiltInSerializerOptions = new()
    {
        TypeInfoResolver = SourceGenerationContext.Default,
        Converters =
        {
            RequestIdSTJsonConverter.Instance,
        },
    };

    /// <summary>
    /// UTF-8 encoding without a preamble.
    /// </summary>
    private static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonRpcProxyOptions DefaultRpcMarshalableProxyOptions = new JsonRpcProxyOptions(JsonRpcProxyOptions.Default) { AcceptProxyWithExtraInterfaces = true, IsFrozen = true };

    private readonly Dictionary<Type, IGenericTypeArgStore> genericLifts = [];

    private readonly ToStringHelper serializationToStringHelper = new ToStringHelper();

    private JsonSerializerOptions massagedUserDataSerializerOptions;

    /// <summary>
    /// Retains the message currently being deserialized so that it can be disposed when we're done with it.
    /// </summary>
    private JsonDocument? deserializingDocument;

    /// <summary>
    /// Initializes a new instance of the <see cref="PolyTypeJsonFormatter"/> class.
    /// </summary>
    public PolyTypeJsonFormatter()
    {
        // Prepare for built-in behavior.
        this.RegisterGenericType<IDisposable>();

        // Take care with any options set *here* instead of in MassageUserDataSerializerOptions,
        // because any settings made only here will be erased if the user changes the JsonSerializerOptions property.
        this.massagedUserDataSerializerOptions = this.MassageUserDataSerializerOptions(new()
        {
        });
    }

    /// <summary>
    /// Gets the shape provider for user data types.
    /// </summary>
    public required ITypeShapeProvider TypeShapeProvider { get; init; }

    /// <inheritdoc/>
    public Encoding Encoding
    {
        get => DefaultEncoding;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Gets or sets the options to use when serializing and deserializing JSON containing user data.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions
    {
        get => this.massagedUserDataSerializerOptions;
        set => this.massagedUserDataSerializerOptions = this.MassageUserDataSerializerOptions(new(value));
    }

    /// <inheritdoc/>
    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer) => this.Deserialize(contentBuffer, this.Encoding);

    /// <inheritdoc/>
    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer, Encoding encoding)
    {
        if (encoding is not UTF8Encoding)
        {
            throw new NotSupportedException("Only our default encoding is supported.");
        }

        JsonDocument document = this.deserializingDocument = JsonDocument.Parse(contentBuffer, DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected a JSON object at the root of the message.");
        }

        JsonRpcMessage message;
        if (document.RootElement.TryGetProperty(Utf8Strings.method, out JsonElement methodElement))
        {
            JsonRpcRequest request = new(this)
            {
                RequestId = ReadRequestId(),
                Method = methodElement.GetString(),
                JsonArguments = document.RootElement.TryGetProperty(Utf8Strings.@params, out JsonElement paramsElement) ? paramsElement : null,
                TraceParent = document.RootElement.TryGetProperty(Utf8Strings.traceparent, out JsonElement traceParentElement) ? traceParentElement.GetString() : null,
                TraceState = document.RootElement.TryGetProperty(Utf8Strings.tracestate, out JsonElement traceStateElement) ? traceStateElement.GetString() : null,
            };
            message = request;
        }
        else if (document.RootElement.TryGetProperty(Utf8Strings.result, out JsonElement resultElement))
        {
            JsonRpcResult result = new(this)
            {
                RequestId = ReadRequestId(),
                JsonResult = resultElement,
            };
            message = result;
        }
        else if (document.RootElement.TryGetProperty(Utf8Strings.error, out JsonElement errorElement))
        {
            JsonRpcError error = new(this)
            {
                RequestId = ReadRequestId(),
                Error = new JsonRpcError.ErrorDetail(this)
                {
                    Code = (JsonRpcErrorCode)errorElement.GetProperty(Utf8Strings.code).GetInt64(),
                    Message = errorElement.GetProperty(Utf8Strings.message).GetString(),
                    JsonData = errorElement.TryGetProperty(Utf8Strings.data, out JsonElement dataElement) ? dataElement : null,
                },
            };

            message = error;
        }
        else
        {
            throw new JsonException("Expected a request, result, or error message.");
        }

        message.Version = document.RootElement.TryGetProperty(Utf8Strings.jsonrpc, out JsonElement jsonRpcElement)
            ? (jsonRpcElement.ValueEquals(Utf8Strings.v2_0) ? "2.0" : (jsonRpcElement.GetString() ?? throw new JsonException("Unexpected null value for jsonrpc property.")))
            : "1.0";

        if (message is IMessageWithTopLevelPropertyBag messageWithTopLevelPropertyBag)
        {
            messageWithTopLevelPropertyBag.TopLevelPropertyBag = new TopLevelPropertyBag(document, this.massagedUserDataSerializerOptions);
        }

        RequestId ReadRequestId()
        {
            return document.RootElement.TryGetProperty(Utf8Strings.id, out JsonElement idElement)
                ? idElement.Deserialize(SourceGenerationContext.Default.RequestId)
            : RequestId.NotSpecified;
        }

        IJsonRpcTracingCallbacks? tracingCallbacks = this.JsonRpc;
        tracingCallbacks?.OnMessageDeserialized(message, document.RootElement);

        this.TryHandleSpecialIncomingMessage(message);

        return message;
    }

    /// <inheritdoc/>
    public object GetJsonText(JsonRpcMessage message) => throw new NotSupportedException();

    /// <inheritdoc/>
    public void Serialize(IBufferWriter<byte> bufferWriter, JsonRpcMessage message)
    {
        Requires.NotNull(message);

        using (this.TrackSerialization(message))
        {
            try
            {
                using Utf8JsonWriter writer = new(bufferWriter, WriterOptions);
                writer.WriteStartObject();
                WriteVersion();
                switch (message)
                {
                    case Protocol.JsonRpcRequest request:
                        WriteId(request.RequestId);
                        writer.WriteString(Utf8Strings.method, request.Method);
                        WriteArguments(request);
                        if (request.TraceParent is not null)
                        {
                            writer.WriteString(Utf8Strings.traceparent, request.TraceParent);
                        }

                        if (request.TraceState is not null)
                        {
                            writer.WriteString(Utf8Strings.tracestate, request.TraceState);
                        }

                        break;
                    case Protocol.JsonRpcResult result:
                        WriteId(result.RequestId);
                        WriteResult(result);
                        break;
                    case Protocol.JsonRpcError error:
                        WriteId(error.RequestId);
                        WriteError(error);
                        break;
                    default:
                        throw new ArgumentException("Unknown message type: " + message.GetType().Name, nameof(message));
                }

                if (message is IMessageWithTopLevelPropertyBag { TopLevelPropertyBag: TopLevelPropertyBag propertyBag })
                {
                    propertyBag.WriteProperties(writer);
                }

                writer.WriteEndObject();

                void WriteVersion()
                {
                    switch (message.Version)
                    {
                        case "1.0":
                            // The 1.0 protocol didn't include the version property at all.
                            break;
                        case "2.0":
                            writer.WriteString(Utf8Strings.jsonrpc, Utf8Strings.v2_0);
                            break;
                        default:
                            writer.WriteString(Utf8Strings.jsonrpc, message.Version);
                            break;
                    }
                }

                void WriteId(RequestId id)
                {
                    if (!id.IsEmpty)
                    {
                        writer.WritePropertyName(Utf8Strings.id);
                        RequestIdSTJsonConverter.Instance.Write(writer, id, BuiltInSerializerOptions);
                    }
                }

                void WriteArguments(Protocol.JsonRpcRequest request)
                {
                    if (request.ArgumentsList is not null)
                    {
                        writer.WriteStartArray(Utf8Strings.@params);
                        for (int i = 0; i < request.ArgumentsList.Count; i++)
                        {
                            WriteUserData(request.ArgumentsList[i], request.ArgumentListDeclaredTypes?[i]);
                        }

                        writer.WriteEndArray();
                    }
                    else if (request.NamedArguments is not null)
                    {
                        writer.WriteStartObject(Utf8Strings.@params);
                        foreach (KeyValuePair<string, object?> argument in request.NamedArguments)
                        {
                            writer.WritePropertyName(argument.Key);
                            WriteUserData(argument.Value, request.NamedArgumentDeclaredTypes?[argument.Key]);
                        }

                        writer.WriteEndObject();
                    }
                    else if (request.Arguments is not null)
                    {
                        // This is a custom named arguments object, so we'll just serialize it as-is.
                        writer.WritePropertyName(Utf8Strings.@params);
                        WriteUserData(request.Arguments, declaredType: null);
                    }
                }

                void WriteResult(Protocol.JsonRpcResult result)
                {
                    writer.WritePropertyName(Utf8Strings.result);
                    WriteUserData(result.Result, result.ResultDeclaredType);
                }

                void WriteError(Protocol.JsonRpcError error)
                {
                    if (error.Error is null)
                    {
                        throw new ArgumentException($"{nameof(error.Error)} property must be set.", nameof(message));
                    }

                    writer.WriteStartObject(Utf8Strings.error);
                    writer.WriteNumber(Utf8Strings.code, (int)error.Error.Code);
                    writer.WriteString(Utf8Strings.message, error.Error.Message);
                    if (error.Error.Data is not null)
                    {
                        writer.WritePropertyName(Utf8Strings.data);
                        WriteUserData(error.Error.Data, null);
                    }

                    writer.WriteEndObject();
                }

                void WriteUserData(object? value, Type? declaredType)
                {
                    if (value is null)
                    {
                        writer.WriteNullValue();
                    }
                    else if (declaredType is not null && declaredType != typeof(void) && declaredType != typeof(object))
                    {
                        JsonTypeInfo typeInfo = this.GetTypeInfoFromBuiltInOrUser(declaredType);
                        JsonSerializer.Serialize(writer, value, typeInfo);
                    }
                    else
                    {
                        Type normalizedType = NormalizeType(value.GetType());
                        JsonTypeInfo typeInfo = this.GetTypeInfoFromBuiltInOrUser(normalizedType);
                        JsonSerializer.Serialize(writer, value, typeInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new JsonException(Resources.SerializationFailure, ex);
            }
        }
    }

    /// <summary>
    /// Registers a type that may be used as a generic type argument for some generic value to be serialized,
    /// such as <see cref="IProgress{T}"/> or <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    /// <typeparam name="T">The type argument to some generic type.</typeparam>
    public void RegisterGenericType<T>()
    {
        this.ThrowIfInitialized();
        if (this.genericLifts.ContainsKey(typeof(T)))
        {
            return;
        }

        this.genericLifts.Add(typeof(T), new GenericTypeArgStore<T>());
    }

    void IJsonRpcFormatterTracingCallbacks.OnSerializationComplete(JsonRpcMessage message, ReadOnlySequence<byte> encodedMessage)
    {
        IJsonRpcTracingCallbacks? tracingCallbacks = this.JsonRpc;
        this.serializationToStringHelper.Activate(encodedMessage);
        try
        {
            tracingCallbacks?.OnMessageSerialized(message, this.serializationToStringHelper);
        }
        finally
        {
            this.serializationToStringHelper.Deactivate();
        }
    }

    Protocol.JsonRpcRequest IJsonRpcMessageFactory.CreateRequestMessage() => new JsonRpcRequest(this);

    Protocol.JsonRpcError IJsonRpcMessageFactory.CreateErrorMessage() => new JsonRpcError(this);

    Protocol.JsonRpcResult IJsonRpcMessageFactory.CreateResultMessage() => new JsonRpcResult(this);

    /// <inheritdoc/>
    private protected override MessageFormatterRpcMarshaledContextTracker CreateMessageFormatterRpcMarshaledContextTracker(JsonRpc rpc) => new MessageFormatterRpcMarshaledContextTracker.PolyTypeShape(rpc, ProxyFactory, this, this.TypeShapeProvider);

    private JsonTypeInfo GetTypeInfoFromBuiltInOrUser(Type type) => BuiltInSerializerOptions.TryGetTypeInfo(type, out JsonTypeInfo? typeInfo) ? typeInfo : this.massagedUserDataSerializerOptions.GetTypeInfo(type);

    private JsonSerializerOptions MassageUserDataSerializerOptions(JsonSerializerOptions options)
    {
        // This is required for $/cancelRequest messages.
        options.Converters.Add(RequestIdSTJsonConverter.Instance);

        // Add support for exotic types.
        options.Converters.Add(new ProgressConverterFactory(this));
        options.Converters.Add(new AsyncEnumerableConverter(this));
        options.Converters.Add(new RpcMarshalableConverterFactory(this));
        options.Converters.Add(new DuplexPipeConverter(this));
        options.Converters.Add(new PipeReaderConverter(this));
        options.Converters.Add(new PipeWriterConverter(this));
        options.Converters.Add(new StreamConverter(this));

        // Add support for serializing exceptions.
        options.Converters.Add(new ExceptionConverter(this));

        return options;
    }

    private object? GenericMethodInvoke(Type typeArg, IGenericTypeArgAssist assist, object? state = null)
    {
        if (!this.genericLifts.TryGetValue(typeArg, out IGenericTypeArgStore? lift))
        {
            throw new NotImplementedException($"{nameof(this.RegisterGenericType)}<T>() must be called first with type argument: {typeArg}.");
        }

        return lift.Invoke(assist, state);
    }

    private static class Utf8Strings
    {
#pragma warning disable SA1300 // Element should begin with upper-case letter
        internal static ReadOnlySpan<byte> jsonrpc => "jsonrpc"u8;

        internal static ReadOnlySpan<byte> v2_0 => "2.0"u8;

        internal static ReadOnlySpan<byte> id => "id"u8;

        internal static ReadOnlySpan<byte> method => "method"u8;

        internal static ReadOnlySpan<byte> @params => "params"u8;

        internal static ReadOnlySpan<byte> traceparent => "traceparent"u8;

        internal static ReadOnlySpan<byte> tracestate => "tracestate"u8;

        internal static ReadOnlySpan<byte> result => "result"u8;

        internal static ReadOnlySpan<byte> error => "error"u8;

        internal static ReadOnlySpan<byte> code => "code"u8;

        internal static ReadOnlySpan<byte> message => "message"u8;

        internal static ReadOnlySpan<byte> data => "data"u8;
#pragma warning restore SA1300 // Element should begin with upper-case letter
    }

    private class TopLevelPropertyBag : TopLevelPropertyBagBase
    {
        private readonly JsonDocument? incomingMessage;
        private readonly JsonSerializerOptions jsonSerializerOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for use with an incoming message.
        /// </summary>
        /// <param name="incomingMessage">The incoming message.</param>
        /// <param name="jsonSerializerOptions">The serializer options to use.</param>
        internal TopLevelPropertyBag(JsonDocument incomingMessage, JsonSerializerOptions jsonSerializerOptions)
            : base(isOutbound: false)
        {
            this.incomingMessage = incomingMessage;
            this.jsonSerializerOptions = jsonSerializerOptions;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for use with an outcoming message.
        /// </summary>
        /// <param name="jsonSerializerOptions">The serializer options to use.</param>
        internal TopLevelPropertyBag(JsonSerializerOptions jsonSerializerOptions)
            : base(isOutbound: true)
        {
            this.jsonSerializerOptions = jsonSerializerOptions;
        }

        internal void WriteProperties(Utf8JsonWriter writer)
        {
            if (this.incomingMessage is not null)
            {
                // We're actually re-transmitting an incoming message (remote target feature).
                // We need to copy all the properties that were in the original message.
                // Don't implement this without enabling the tests for the scenario found in JsonRpcRemoteTargetPolyTypeJsonFormatterTests.cs.
                // The tests fail for reasons even without this support, so there's work to do beyond just implementing this.
                throw new NotImplementedException();
            }
            else
            {
                foreach (KeyValuePair<string, (Type DeclaredType, object? Value)> property in this.OutboundProperties)
                {
                    writer.WritePropertyName(property.Key);
                    JsonTypeInfo typeInfo = this.jsonSerializerOptions.GetTypeInfo(property.Value.DeclaredType);
                    JsonSerializer.Serialize(writer, property.Value.Value, typeInfo);
                }
            }
        }

        protected internal override bool TryGetTopLevelProperty<T>(string name, [MaybeNull] out T value)
        {
            if (this.incomingMessage?.RootElement.TryGetProperty(name, out JsonElement serializedValue) is true)
            {
                value = serializedValue.Deserialize<T>((JsonTypeInfo<T>)this.jsonSerializerOptions.GetTypeInfo(typeof(T)));
                return true;
            }

            value = default;
            return false;
        }
    }

    private class JsonRpcRequest : JsonRpcRequestBase
    {
        private readonly PolyTypeJsonFormatter formatter;

        private int? argumentCount;

        private JsonElement? jsonArguments;

        internal JsonRpcRequest(PolyTypeJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override int ArgumentCount => this.argumentCount ?? base.ArgumentCount;

        public override IEnumerable<string>? ArgumentNames
        {
            get
            {
                return this.JsonArguments?.ValueKind is JsonValueKind.Object
                    ? this.JsonArguments.Value.EnumerateObject().Select(p => p.Name)
                    : null;
            }
        }

        internal JsonElement? JsonArguments
        {
            get => this.jsonArguments;
            init
            {
                this.jsonArguments = value;
                if (value.HasValue)
                {
                    this.argumentCount = CountArguments(value.Value);
                }
            }
        }

        public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, Span<object?> typedArguments)
        {
            using (this.formatter.TrackDeserialization(this, parameters))
            {
                // Support for opt-in to deserializing all named arguments into a single parameter.
                if (parameters.Length == 1 && this.formatter.ApplicableMethodAttributeOnDeserializingMethod?.UseSingleObjectParameterDeserialization is true && this.JsonArguments is not null)
                {
                    typedArguments[0] = this.JsonArguments.Value.Deserialize(this.formatter.GetTypeInfoFromBuiltInOrUser(parameters[0].ParameterType));
                    return ArgumentMatchResult.Success;
                }

                return base.TryGetTypedArguments(parameters, typedArguments);
            }
        }

        public override bool TryGetArgumentByNameOrIndex(string? name, int position, Type? typeHint, out object? value)
        {
            if (this.JsonArguments is null)
            {
                value = null;
                return false;
            }

            JsonElement? valueElement = null;
            switch (this.JsonArguments?.ValueKind)
            {
                case JsonValueKind.Object when name is not null:
                    if (this.JsonArguments.Value.TryGetProperty(name, out JsonElement propertyValue))
                    {
                        valueElement = propertyValue;
                    }

                    break;
                case JsonValueKind.Array when position >= 0:
                    int elementIndex = 0;
                    foreach (JsonElement arrayElement in this.JsonArguments.Value.EnumerateArray())
                    {
                        if (elementIndex++ == position)
                        {
                            valueElement = arrayElement;
                            break;
                        }
                    }

                    break;
            }

            try
            {
                using (this.formatter.TrackDeserialization(this))
                {
                    try
                    {
                        value = valueElement?.Deserialize(this.formatter.GetTypeInfoFromBuiltInOrUser(typeHint ?? typeof(object)));
                    }
                    catch (Exception ex)
                    {
                        if (this.formatter.JsonRpc?.TraceSource.Switch.ShouldTrace(TraceEventType.Warning) ?? false)
                        {
                            this.formatter.JsonRpc.TraceSource.TraceEvent(TraceEventType.Warning, (int)JsonRpc.TraceEvents.MethodArgumentDeserializationFailure, Resources.FailureDeserializingRpcArgument, name, position, typeHint, ex);
                        }

                        throw new RpcArgumentDeserializationException(name, position, typeHint, ex);
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new RpcArgumentDeserializationException(name, position, typeHint, ex);
            }

            return valueElement.HasValue;
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatter.massagedUserDataSerializerOptions);

        protected override void ReleaseBuffers()
        {
            base.ReleaseBuffers();
            this.jsonArguments = null;
            this.formatter.deserializingDocument?.Dispose();
            this.formatter.deserializingDocument = null;
        }

        private static int CountArguments(JsonElement arguments)
        {
            int count;
            switch (arguments.ValueKind)
            {
                case JsonValueKind.Array:
                    count = arguments.GetArrayLength();

                    break;
                case JsonValueKind.Object:
                    count = 0;
                    foreach (JsonProperty property in arguments.EnumerateObject())
                    {
                        count++;
                    }

                    break;
                default:
                    throw new InvalidOperationException("Unexpected value kind: " + arguments.ValueKind);
            }

            return count;
        }
    }

    private class JsonRpcResult : JsonRpcResultBase
    {
        private readonly PolyTypeJsonFormatter formatter;

        private Exception? resultDeserializationException;

        internal JsonRpcResult(PolyTypeJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        internal JsonElement? JsonResult { get; set; }

        public override T GetResult<T>()
        {
            if (this.resultDeserializationException is not null)
            {
                ExceptionDispatchInfo.Capture(this.resultDeserializationException).Throw();
            }

            return this.JsonResult is null
                ? (T)this.Result!
                : this.JsonResult.Value.Deserialize((JsonTypeInfo<T>)this.formatter.massagedUserDataSerializerOptions.GetTypeInfo(typeof(T)))!;
        }

        protected internal override void SetExpectedResultType(Type resultType)
        {
            Verify.Operation(this.JsonResult is not null, "Result is no longer available or has already been deserialized.");

            try
            {
                using (this.formatter.TrackDeserialization(this))
                {
                    this.Result = this.JsonResult.Value.Deserialize(this.formatter.massagedUserDataSerializerOptions.GetTypeInfo(resultType));
                }

                this.JsonResult = default;
            }
            catch (Exception ex)
            {
                // This was a best effort anyway. We'll throw again later at a more convenient time for JsonRpc.
                this.resultDeserializationException = new JsonException(string.Format(CultureInfo.CurrentCulture, Resources.FailureDeserializingRpcResult, resultType.Name, ex.GetType().Name, ex.Message), ex);
            }
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatter.massagedUserDataSerializerOptions);

        protected override void ReleaseBuffers()
        {
            base.ReleaseBuffers();
            this.JsonResult = null;
            this.formatter.deserializingDocument?.Dispose();
            this.formatter.deserializingDocument = null;
        }
    }

    private class JsonRpcError : JsonRpcErrorBase
    {
        private readonly PolyTypeJsonFormatter formatter;

        public JsonRpcError(PolyTypeJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        internal new ErrorDetail? Error
        {
            get => (ErrorDetail?)base.Error;
            set => base.Error = value;
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatter.massagedUserDataSerializerOptions);

        protected override void ReleaseBuffers()
        {
            base.ReleaseBuffers();
            if (this.Error is { } detail)
            {
                detail.JsonData = null;
            }

            this.formatter.deserializingDocument?.Dispose();
            this.formatter.deserializingDocument = null;
        }

        internal new class ErrorDetail : Protocol.JsonRpcError.ErrorDetail
        {
            private readonly PolyTypeJsonFormatter formatter;

            internal ErrorDetail(PolyTypeJsonFormatter formatter)
            {
                this.formatter = formatter;
            }

            internal JsonElement? JsonData { get; set; }

            public override object? GetData(Type dataType)
            {
                Requires.NotNull(dataType, nameof(dataType));
                if (this.JsonData is null)
                {
                    return this.Data;
                }

                try
                {
                    return this.JsonData.Value.Deserialize(this.formatter.GetTypeInfoFromBuiltInOrUser(dataType));
                }
                catch (JsonException)
                {
                    // Deserialization failed. Try returning array/dictionary based primitive objects.
                    try
                    {
                        return this.JsonData.Value.Deserialize(SourceGenerationContext.Default.Object);
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                }
            }

            protected internal override void SetExpectedDataType(Type dataType)
            {
                this.Data = this.GetData(dataType);

                // Clear the source now that we've deserialized to prevent GetData from attempting
                // deserialization later when the buffer may be recycled on another thread.
                this.JsonData = default;
            }
        }
    }

    private class ProgressConverterFactory : JsonConverterFactory, IGenericTypeArgAssist
    {
        private readonly PolyTypeJsonFormatter formatter;

        internal ProgressConverterFactory(PolyTypeJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override bool CanConvert(Type typeToConvert) => TrackerHelpers.FindIProgressInterfaceImplementedBy(typeToConvert) is not null;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            Type? iface = TrackerHelpers.FindIProgressInterfaceImplementedBy(typeToConvert);
            Assumes.NotNull(iface);
            return (JsonConverter)this.formatter.GenericMethodInvoke(iface.GetGenericArguments()[0], this)!;
        }

        object IGenericTypeArgAssist.Invoke<T>(object? state) => new Converter<T>(this.formatter);

        private class Converter<T> : JsonConverter<IProgress<T>>
        {
            private readonly PolyTypeJsonFormatter formatter;

            public Converter(PolyTypeJsonFormatter formatter)
            {
                this.formatter = formatter;
            }

            public override IProgress<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                Assumes.NotNull(this.formatter.JsonRpc);
                object token = reader.TokenType switch
                {
                    JsonTokenType.String => reader.GetString()!,
                    JsonTokenType.Number => reader.GetInt64(),
                    _ => throw new NotSupportedException("Unsupported token type."), // Ideally, we should *copy* the token so we can retain it and replay it later.
                };

                bool clientRequiresNamedArgs = this.formatter.ApplicableMethodAttributeOnDeserializingMethod is { ClientRequiresNamedArguments: true };
                return (IProgress<T>)this.formatter.FormatterProgressTracker.CreateProgress<T>(this.formatter.JsonRpc, token, clientRequiresNamedArgs);
            }

            public override void Write(Utf8JsonWriter writer, IProgress<T> value, JsonSerializerOptions options)
            {
                writer.WriteNumberValue(this.formatter.FormatterProgressTracker.GetTokenForProgress(value));
            }
        }
    }

    private class AsyncEnumerableConverter : JsonConverterFactory, IGenericTypeArgAssist
    {
        private readonly PolyTypeJsonFormatter formatter;

        internal AsyncEnumerableConverter(PolyTypeJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override bool CanConvert(Type typeToConvert) => TrackerHelpers.FindIAsyncEnumerableInterfaceImplementedBy(typeToConvert) is not null;

        public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            Type? iface = TrackerHelpers.FindIAsyncEnumerableInterfaceImplementedBy(typeToConvert);
            Assumes.NotNull(iface);
            return (JsonConverter)this.formatter.GenericMethodInvoke(iface.GetGenericArguments()[0], this)!;
        }

        object? IGenericTypeArgAssist.Invoke<T>(object? state) => new Converter<T>(this.formatter);

        private class Converter<T> : JsonConverter<IAsyncEnumerable<T>>
        {
            private readonly PolyTypeJsonFormatter formatter;

            public Converter(PolyTypeJsonFormatter formatter)
            {
                this.formatter = formatter;
            }

            public override IAsyncEnumerable<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                using JsonDocument wrapper = JsonDocument.ParseValue(ref reader);
                JsonElement? handle = null;
                if (wrapper.RootElement.TryGetProperty(MessageFormatterEnumerableTracker.TokenPropertyName, out JsonElement enumToken))
                {
                    // Copy the token so we can retain it and replay it later.
                    handle = enumToken.Clone();
                }

                IReadOnlyList<T>? prefetchedItems = null;
                if (wrapper.RootElement.TryGetProperty(MessageFormatterEnumerableTracker.ValuesPropertyName, out JsonElement prefetchedElement))
                {
                    prefetchedItems = prefetchedElement.Deserialize((JsonTypeInfo<IReadOnlyList<T>>)options.GetTypeInfo(typeof(IReadOnlyList<T>)));
                }

                return this.formatter.EnumerableTracker.CreateEnumerableProxy(handle, prefetchedItems);
            }

            public override void Write(Utf8JsonWriter writer, IAsyncEnumerable<T> value, JsonSerializerOptions options)
            {
                (IReadOnlyList<T> Elements, bool Finished) prefetched = value.TearOffPrefetchedElements();
                long token = this.formatter.EnumerableTracker.GetToken(value);
                writer.WriteStartObject();
                if (!prefetched.Finished)
                {
                    writer.WriteNumber(MessageFormatterEnumerableTracker.TokenPropertyName, token);
                }

                if (prefetched.Elements.Count > 0)
                {
                    writer.WritePropertyName(MessageFormatterEnumerableTracker.ValuesPropertyName);
                    JsonSerializer.Serialize(writer, prefetched.Elements, options.GetTypeInfo(typeof(IReadOnlyList<T>)));
                }

                writer.WriteEndObject();
            }
        }
    }

    private class RpcMarshalableConverterFactory : JsonConverterFactory, IGenericTypeArgAssist
    {
        private readonly PolyTypeJsonFormatter formatter;

        public RpcMarshalableConverterFactory(PolyTypeJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override bool CanConvert(Type typeToConvert) => MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(this.formatter.TypeShapeProvider.GetTypeShapeOrThrow(typeToConvert), DefaultRpcMarshalableProxyOptions, out _, out _, out _);

        public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            return (JsonConverter)this.formatter.GenericMethodInvoke(typeToConvert, this)!;
        }

        object? IGenericTypeArgAssist.Invoke<T>(object? state) => new Converter<T>(this.formatter);

        private class Converter<T> : JsonConverter<T>
        {
            private readonly PolyTypeJsonFormatter formatter;
            private readonly JsonRpcProxyOptions proxyOptions;
            private readonly JsonRpcTargetOptions targetOptions;
            private readonly RpcMarshalableAttribute rpcMarshalableAttribute;

            public Converter(PolyTypeJsonFormatter formatter)
            {
                Assumes.True(MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(
                    formatter.TypeShapeProvider.GetTypeShapeOrThrow(typeof(T)),
                    DefaultRpcMarshalableProxyOptions,
                    out JsonRpcProxyOptions? proxyOptions,
                    out JsonRpcTargetOptions? targetOptions,
                    out RpcMarshalableAttribute? attribute));

                this.formatter = formatter;
                this.proxyOptions = proxyOptions;
                this.targetOptions = targetOptions;
                this.rpcMarshalableAttribute = attribute;
            }

            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                MessageFormatterRpcMarshaledContextTracker.MarshalToken token = JsonSerializer.Deserialize(ref reader, SourceGenerationContext.Default.MarshalToken);
                return (T)this.formatter.RpcMarshaledContextTracker.GetObject(typeof(T), token, this.proxyOptions);
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                RpcTargetMetadata mapping = RpcTargetMetadata.FromShape(this.formatter.TypeShapeProvider.GetTypeShapeOrThrow(typeof(T)));
                MessageFormatterRpcMarshaledContextTracker.MarshalToken token = this.formatter.RpcMarshaledContextTracker.GetToken(value!, this.targetOptions, mapping, this.rpcMarshalableAttribute);
                JsonSerializer.Serialize(writer, token, SourceGenerationContext.Default.MarshalToken);
            }
        }
    }

    private class DuplexPipeConverter : JsonConverter<IDuplexPipe>
    {
        private readonly PolyTypeJsonFormatter formatter;

        internal DuplexPipeConverter(PolyTypeJsonFormatter formatter)
        {
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        }

        public override bool CanConvert(Type typeToConvert) => typeof(IDuplexPipe).IsAssignableFrom(typeToConvert);

        public override IDuplexPipe Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return this.formatter.DuplexPipeTracker.GetPipe(reader.GetUInt64());
        }

        public override void Write(Utf8JsonWriter writer, IDuplexPipe value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(this.formatter.DuplexPipeTracker.GetULongToken(value).Value);
        }
    }

    private class PipeReaderConverter : JsonConverter<PipeReader>
    {
        private readonly PolyTypeJsonFormatter formatter;

        internal PipeReaderConverter(PolyTypeJsonFormatter formatter)
        {
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        }

        public override bool CanConvert(Type typeToConvert) => typeof(PipeReader).IsAssignableFrom(typeToConvert);

        public override PipeReader Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return this.formatter.DuplexPipeTracker!.GetPipeReader(reader.GetUInt64());
        }

        public override void Write(Utf8JsonWriter writer, PipeReader value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(this.formatter.DuplexPipeTracker.GetULongToken(value).Value);
        }
    }

    private class PipeWriterConverter : JsonConverter<PipeWriter>
    {
        private readonly PolyTypeJsonFormatter formatter;

        internal PipeWriterConverter(PolyTypeJsonFormatter formatter)
        {
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        }

        public override bool CanConvert(Type typeToConvert) => typeof(PipeWriter).IsAssignableFrom(typeToConvert);

        public override PipeWriter Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return this.formatter.DuplexPipeTracker.GetPipeWriter(reader.GetUInt64());
        }

        public override void Write(Utf8JsonWriter writer, PipeWriter value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(this.formatter.DuplexPipeTracker.GetULongToken(value).Value);
        }
    }

    private class StreamConverter : JsonConverter<Stream>
    {
        private readonly PolyTypeJsonFormatter formatter;

        internal StreamConverter(PolyTypeJsonFormatter formatter)
        {
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        }

        public override bool CanConvert(Type typeToConvert) => typeof(Stream).IsAssignableFrom(typeToConvert);

        public override Stream Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return this.formatter.DuplexPipeTracker.GetPipe(reader.GetUInt64()).AsStream();
        }

        public override void Write(Utf8JsonWriter writer, Stream value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(this.formatter.DuplexPipeTracker.GetULongToken(value.UsePipe()).Value);
        }
    }

    private class ExceptionConverter : JsonConverter<Exception>
    {
        /// <summary>
        /// Tracks recursion count while serializing or deserializing an exception.
        /// </summary>
        private static readonly ThreadLocal<int> ExceptionRecursionCounter = new();

        private readonly PolyTypeJsonFormatter formatter;

        internal ExceptionConverter(PolyTypeJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override bool CanConvert(Type typeToConvert) => typeof(Exception).IsAssignableFrom(typeToConvert);

        public override Exception? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Assumes.NotNull(this.formatter.JsonRpc);

            ExceptionRecursionCounter.Value++;
            try
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new InvalidOperationException("Expected a StartObject token.");
                }

                if (ExceptionRecursionCounter.Value > this.formatter.JsonRpc.ExceptionOptions.RecursionLimit)
                {
                    // Exception recursion has gone too deep. Skip this value and return null as if there were no inner exception.
                    // Note that in skipping, the parser may use recursion internally and may still throw if its own limits are exceeded.
                    reader.Skip();
                    return null;
                }

                JsonNode? jsonNode = JsonNode.Parse(ref reader) ?? throw new JsonException("Unexpected null");
                SerializationInfo? info = new SerializationInfo(typeToConvert, new JsonConverterFormatter(this.formatter.massagedUserDataSerializerOptions));
                foreach (KeyValuePair<string, JsonNode?> property in jsonNode.AsObject())
                {
                    info.AddSafeValue(property.Key, property.Value);
                }

                return ExceptionSerializationHelpers.Deserialize<Exception>(this.formatter.JsonRpc, info, this.formatter.JsonRpc, this.formatter.JsonRpc?.TraceSource);
            }
            finally
            {
                ExceptionRecursionCounter.Value--;
            }
        }

        public override void Write(Utf8JsonWriter writer, Exception value, JsonSerializerOptions options)
        {
            // We have to guard our own recursion because the serializer has no visibility into inner exceptions.
            // Each exception in the russian doll is a new serialization job from its perspective.
            ExceptionRecursionCounter.Value++;
            try
            {
                if (ExceptionRecursionCounter.Value > this.formatter.JsonRpc?.ExceptionOptions.RecursionLimit)
                {
                    // Exception recursion has gone too deep. Skip this value and write null as if there were no inner exception.
                    writer.WriteNullValue();
                    return;
                }

                SerializationInfo info = new SerializationInfo(value.GetType(), new JsonConverterFormatter(this.formatter.massagedUserDataSerializerOptions));
                ExceptionSerializationHelpers.Serialize(value, info);
                writer.WriteStartObject();
                foreach (SerializationEntry element in info.GetSafeMembers())
                {
                    writer.WritePropertyName(element.Name);
                    if (element.Value is null)
                    {
                        writer.WriteNullValue();
                    }
                    else if (element.ObjectType == typeof(System.Collections.IDictionary))
                    {
                        // Some exception types tuck data into this dictionary that is assumed to be safe to read back (e.g. use a BinaryFormatter to interpret its data).
                        // Also, it's difficult to safely deserialize an untyped dictionary because we don't have an hash-collision resistant key comparer for System.Object.
                        // So just skip it.
                        writer.WriteNullValue();
                    }
                    else
                    {
                        // We prefer the declared type but will fallback to the runtime type.
                        Type preferredType = NormalizeType(element.ObjectType);
                        Type fallbackType = NormalizeType(element.Value.GetType());
                        if (!this.formatter.massagedUserDataSerializerOptions.TryGetTypeInfo(preferredType, out JsonTypeInfo? typeInfo) &&
                            !this.formatter.massagedUserDataSerializerOptions.TryGetTypeInfo(fallbackType, out typeInfo))
                        {
                            throw new NotSupportedException($"Unable to find JsonTypeInfo for {preferredType} or {fallbackType}.");
                        }

                        JsonSerializer.Serialize(writer, element.Value, typeInfo);
                    }
                }

                writer.WriteEndObject();
            }
            catch (Exception ex)
            {
                throw new JsonException(ex.Message, ex);
            }
            finally
            {
                ExceptionRecursionCounter.Value--;
            }
        }
    }

    private class JsonConverterFormatter : IFormatterConverter
    {
        private readonly JsonSerializerOptions serializerOptions;

        internal JsonConverterFormatter(JsonSerializerOptions serializerOptions)
        {
            this.serializerOptions = serializerOptions;
        }

#pragma warning disable CS8766 // This method may in fact return null, and no one cares.
        public object? Convert(object value, Type type)
#pragma warning restore CS8766
        {
            var jsonValue = (JsonNode)value;

            if (type == typeof(System.Collections.IDictionary))
            {
                // In this world, we may in fact be returning a null value based on a non-null value.
                return DeserializePrimitive(jsonValue);
            }

            return jsonValue.Deserialize(this.serializerOptions.GetTypeInfo(type))!;
        }

        public object Convert(object value, TypeCode typeCode)
        {
            return typeCode switch
            {
                TypeCode.Object => ((JsonNode)value).Deserialize(this.serializerOptions.GetTypeInfo(typeof(object)))!,
                _ => ExceptionSerializationHelpers.Convert(this, value, typeCode),
            };
        }

        public bool ToBoolean(object value) => ((JsonNode)value).GetValue<bool>();

        public byte ToByte(object value) => ((JsonNode)value).GetValue<byte>();

        public char ToChar(object value) => ((JsonNode)value).GetValue<char>();

        public DateTime ToDateTime(object value) => ((JsonNode)value).GetValue<DateTime>();

        public decimal ToDecimal(object value) => ((JsonNode)value).GetValue<decimal>();

        public double ToDouble(object value) => ((JsonNode)value).GetValue<double>();

        public short ToInt16(object value) => ((JsonNode)value).GetValue<short>();

        public int ToInt32(object value) => ((JsonNode)value).GetValue<int>();

        public long ToInt64(object value) => ((JsonNode)value).GetValue<long>();

        public sbyte ToSByte(object value) => ((JsonNode)value).GetValue<sbyte>();

        public float ToSingle(object value) => ((JsonNode)value).GetValue<float>();

        public string? ToString(object value) => ((JsonNode)value).GetValue<string>();

        public ushort ToUInt16(object value) => ((JsonNode)value).GetValue<ushort>();

        public uint ToUInt32(object value) => ((JsonNode)value).GetValue<uint>();

        public ulong ToUInt64(object value) => ((JsonNode)value).GetValue<ulong>();

        private static object? DeserializePrimitive(JsonNode? node)
        {
            return node switch
            {
                JsonObject o => DeserializeObjectAsDictionary(o),
                JsonValue v => DeserializePrimitive(v.GetValue<JsonElement>()),
                JsonArray a => a.Select(DeserializePrimitive).ToArray(),
                null => null,
                _ => throw new NotSupportedException("Unrecognized node type: " + node.GetType().Name),
            };
        }

        private static Dictionary<string, object?> DeserializeObjectAsDictionary(JsonNode jsonNode)
        {
            Dictionary<string, object?> dictionary = new();
            foreach (KeyValuePair<string, JsonNode?> property in jsonNode.AsObject())
            {
                dictionary.Add(property.Key, DeserializePrimitive(property.Value));
            }

            return dictionary;
        }

        private static object? DeserializePrimitive(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt32(out int intValue) ? intValue : element.GetInt64(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => throw new NotSupportedException(),
            };
        }
    }

    /// <inheritdoc cref="MessagePackFormatter.ToStringHelper"/>
    private class ToStringHelper
    {
        private ReadOnlySequence<byte>? encodedMessage;
        private string? jsonString;

        public override string ToString()
        {
            Verify.Operation(this.encodedMessage.HasValue, "This object has not been activated. It may have already been recycled.");

            using JsonDocument doc = JsonDocument.Parse(this.encodedMessage.Value);
            return this.jsonString ??= doc.RootElement.ToString();
        }

        /// <summary>
        /// Initializes this object to represent a message.
        /// </summary>
        internal void Activate(ReadOnlySequence<byte> encodedMessage)
        {
            this.encodedMessage = encodedMessage;
        }

        /// <summary>
        /// Cleans out this object to release memory and ensure <see cref="ToString"/> throws if someone uses it after deactivation.
        /// </summary>
        internal void Deactivate()
        {
            this.encodedMessage = null;
            this.jsonString = null;
        }
    }
}
