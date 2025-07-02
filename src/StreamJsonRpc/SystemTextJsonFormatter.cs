// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Collections.Concurrent;
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
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// A formatter that emits UTF-8 encoded JSON where user data should be serializable via the <see cref="JsonSerializer"/>.
/// </summary>
[RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
public partial class SystemTextJsonFormatter : FormatterBase, IJsonRpcMessageFormatter, IJsonRpcMessageTextFormatter, IJsonRpcInstanceContainer, IJsonRpcMessageFactory, IJsonRpcFormatterTracingCallbacks
{
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
            RequestIdJsonConverter.Instance,
        },
    };

    /// <summary>
    /// UTF-8 encoding without a preamble.
    /// </summary>
    private static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ToStringHelper serializationToStringHelper = new ToStringHelper();

    private JsonSerializerOptions massagedUserDataSerializerOptions;

    /// <summary>
    /// Retains the message currently being deserialized so that it can be disposed when we're done with it.
    /// </summary>
    private JsonDocument? deserializingDocument;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemTextJsonFormatter"/> class.
    /// </summary>
    public SystemTextJsonFormatter()
    {
        // Take care with any options set *here* instead of in MassageUserDataSerializerOptions,
        // because any settings made only here will be erased if the user changes the JsonSerializerOptions property.
        this.massagedUserDataSerializerOptions = this.MassageUserDataSerializerOptions(new()
        {
        });
    }

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

        if (document.RootElement.TryGetProperty(Utf8Strings.jsonrpc, out JsonElement jsonRpcElement))
        {
            message.Version = jsonRpcElement.ValueEquals(Utf8Strings.v2_0) ? "2.0" : (jsonRpcElement.GetString() ?? throw new JsonException("Unexpected null value for jsonrpc property."));
        }
        else
        {
            // Version 1.0 is implied when it is absent.
            message.Version = "1.0";
        }

        if (message is IMessageWithTopLevelPropertyBag messageWithTopLevelPropertyBag)
        {
            messageWithTopLevelPropertyBag.TopLevelPropertyBag = new TopLevelPropertyBag(document, this.massagedUserDataSerializerOptions);
        }

        RequestId ReadRequestId()
        {
            return document.RootElement.TryGetProperty(Utf8Strings.id, out JsonElement idElement)
                ? idElement.Deserialize<RequestId>(BuiltInSerializerOptions)
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
                        RequestIdJsonConverter.Instance.Write(writer, id, BuiltInSerializerOptions);
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
                    if (declaredType is not null && value is not null)
                    {
                        JsonSerializer.Serialize(writer, value, declaredType, this.massagedUserDataSerializerOptions);
                    }
                    else
                    {
                        JsonSerializer.Serialize(writer, value, this.massagedUserDataSerializerOptions);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new JsonException(Resources.SerializationFailure, ex);
            }
        }
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

    private JsonSerializerOptions MassageUserDataSerializerOptions(JsonSerializerOptions options)
    {
        // This is required for $/cancelRequest messages.
        options.Converters.Add(RequestIdJsonConverter.Instance);

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

    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
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
                // Don't implement this without enabling the tests for the scenario found in JsonRpcRemoteTargetSystemTextJsonFormatterTests.cs.
                // The tests fail for reasons even without this support, so there's work to do beyond just implementing this.
                throw new NotImplementedException();
            }
            else
            {
                foreach (KeyValuePair<string, (Type DeclaredType, object? Value)> property in this.OutboundProperties)
                {
                    writer.WritePropertyName(property.Key);
                    JsonSerializer.Serialize(writer, property.Value.Value, this.jsonSerializerOptions);
                }
            }
        }

        protected internal override bool TryGetTopLevelProperty<T>(string name, [MaybeNull] out T value)
        {
            if (this.incomingMessage?.RootElement.TryGetProperty(name, out JsonElement serializedValue) is true)
            {
                value = serializedValue.Deserialize<T>(this.jsonSerializerOptions);
                return true;
            }

            value = default;
            return false;
        }
    }

    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
    private class JsonRpcRequest : JsonRpcRequestBase
    {
        private readonly SystemTextJsonFormatter formatter;

        private int? argumentCount;

        private JsonElement? jsonArguments;

        internal JsonRpcRequest(SystemTextJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override int ArgumentCount => this.argumentCount ?? base.ArgumentCount;

        public override IEnumerable<string>? ArgumentNames
        {
            get
            {
                if (this.JsonArguments?.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in this.JsonArguments.Value.EnumerateObject())
                        yield return p.Name;
                }
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
                    typedArguments[0] = this.JsonArguments.Value.Deserialize(parameters[0].ParameterType, this.formatter.massagedUserDataSerializerOptions);
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
                        value = valueElement?.Deserialize(typeHint ?? typeof(object), this.formatter.massagedUserDataSerializerOptions);
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

    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
    private class JsonRpcResult : JsonRpcResultBase
    {
        private readonly SystemTextJsonFormatter formatter;

        private Exception? resultDeserializationException;

        internal JsonRpcResult(SystemTextJsonFormatter formatter)
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
                : this.JsonResult.Value.Deserialize<T>(this.formatter.massagedUserDataSerializerOptions)!;
        }

        protected internal override void SetExpectedResultType(Type resultType)
        {
            Verify.Operation(this.JsonResult is not null, "Result is no longer available or has already been deserialized.");

            try
            {
                using (this.formatter.TrackDeserialization(this))
                {
                    this.Result = this.JsonResult.Value.Deserialize(resultType, this.formatter.massagedUserDataSerializerOptions);
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

    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
    private class JsonRpcError : JsonRpcErrorBase
    {
        private readonly SystemTextJsonFormatter formatter;

        public JsonRpcError(SystemTextJsonFormatter formatter)
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

        [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
        internal new class ErrorDetail : Protocol.JsonRpcError.ErrorDetail
        {
            private readonly SystemTextJsonFormatter formatter;

            internal ErrorDetail(SystemTextJsonFormatter formatter)
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
                    return this.JsonData.Value.Deserialize(dataType, this.formatter.massagedUserDataSerializerOptions);
                }
                catch (JsonException)
                {
                    // Deserialization failed. Try returning array/dictionary based primitive objects.
                    try
                    {
                        return this.JsonData.Value.Deserialize<object>(this.formatter.massagedUserDataSerializerOptions);
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

    private class RequestIdJsonConverter : JsonConverter<RequestId>
    {
        internal static readonly RequestIdJsonConverter Instance = new();

        public override RequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.Number => new RequestId(reader.GetInt64()),
                JsonTokenType.String => new RequestId(reader.GetString()),
                JsonTokenType.Null => RequestId.Null,
                _ => throw new JsonException("Unexpected token type for id property: " + reader.TokenType),
            };
        }

        public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options)
        {
            if (value.Number is long idNumber)
            {
                writer.WriteNumberValue(idNumber);
            }
            else if (value.String is string idString)
            {
                writer.WriteStringValue(idString);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }

    [RequiresDynamicCode(RuntimeReasons.Formatters)]
    private class ProgressConverterFactory : JsonConverterFactory
    {
        private readonly SystemTextJsonFormatter formatter;

        internal ProgressConverterFactory(SystemTextJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override bool CanConvert(Type typeToConvert) => TrackerHelpers.FindIProgressInterfaceImplementedBy(typeToConvert) is not null;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            Type? iface = TrackerHelpers.FindIProgressInterfaceImplementedBy(typeToConvert);
            Assumes.NotNull(iface);
            Type genericTypeArg = iface.GetGenericArguments()[0];
            Type converterType = typeof(Converter<>).MakeGenericType(genericTypeArg);
            return (JsonConverter)Activator.CreateInstance(converterType, this.formatter)!;
        }

        [RequiresDynamicCode(RuntimeReasons.CloseGenerics)]
        private class Converter<T> : JsonConverter<IProgress<T>>
        {
            private readonly SystemTextJsonFormatter formatter;

            public Converter(SystemTextJsonFormatter formatter)
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
                return (IProgress<T>)this.formatter.FormatterProgressTracker.CreateProgress(this.formatter.JsonRpc, token, typeToConvert, clientRequiresNamedArgs);
            }

            public override void Write(Utf8JsonWriter writer, IProgress<T> value, JsonSerializerOptions options)
            {
                writer.WriteNumberValue(this.formatter.FormatterProgressTracker.GetTokenForProgress(value));
            }
        }
    }

    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
    private class AsyncEnumerableConverter : JsonConverterFactory
    {
        private readonly SystemTextJsonFormatter formatter;

        internal AsyncEnumerableConverter(SystemTextJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override bool CanConvert(Type typeToConvert) => TrackerHelpers.FindIAsyncEnumerableInterfaceImplementedBy(typeToConvert) is not null;

        public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            Type? iface = TrackerHelpers.FindIAsyncEnumerableInterfaceImplementedBy(typeToConvert);
            Assumes.NotNull(iface);
            Type genericTypeArg = iface.GetGenericArguments()[0];
            Type converterType = typeof(Converter<>).MakeGenericType(genericTypeArg);
            return (JsonConverter)Activator.CreateInstance(converterType, this.formatter)!;
        }

        [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
        private class Converter<T> : JsonConverter<IAsyncEnumerable<T>>
        {
            private readonly SystemTextJsonFormatter formatter;

            public Converter(SystemTextJsonFormatter formatter)
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
                    prefetchedItems = prefetchedElement.Deserialize<IReadOnlyList<T>>(options);
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
                    JsonSerializer.Serialize(writer, prefetched.Elements, options);
                }

                writer.WriteEndObject();
            }
        }
    }

    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
    private class RpcMarshalableConverterFactory : JsonConverterFactory
    {
        private readonly SystemTextJsonFormatter formatter;

        public RpcMarshalableConverterFactory(SystemTextJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override bool CanConvert(Type typeToConvert)
        {
            return MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(typeToConvert, out _, out _, out _);
        }

        public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            Assumes.True(MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(typeToConvert, out JsonRpcProxyOptions? proxyOptions, out JsonRpcTargetOptions? targetOptions, out RpcMarshalableAttribute? attribute));
            return (JsonConverter)Activator.CreateInstance(
                typeof(Converter<>).MakeGenericType(typeToConvert),
                this.formatter,
                proxyOptions,
                targetOptions,
                attribute)!;
        }

        [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
        private class Converter<T>(SystemTextJsonFormatter formatter, JsonRpcProxyOptions proxyOptions, JsonRpcTargetOptions targetOptions, RpcMarshalableAttribute rpcMarshalableAttribute) : JsonConverter<T>
            where T : class
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                MessageFormatterRpcMarshaledContextTracker.MarshalToken token = JsonSerializer.Deserialize<MessageFormatterRpcMarshaledContextTracker.MarshalToken>(ref reader, options);
                return (T)formatter.RpcMarshaledContextTracker.GetObject(typeof(T), token, proxyOptions);
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                MessageFormatterRpcMarshaledContextTracker.MarshalToken token = formatter.RpcMarshaledContextTracker.GetToken(value, targetOptions, typeof(T), rpcMarshalableAttribute);
                JsonSerializer.Serialize(writer, token, options);
            }
        }
    }

    private class DuplexPipeConverter : JsonConverter<IDuplexPipe>
    {
        private readonly SystemTextJsonFormatter formatter;

        internal DuplexPipeConverter(SystemTextJsonFormatter formatter)
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
        private readonly SystemTextJsonFormatter formatter;

        internal PipeReaderConverter(SystemTextJsonFormatter formatter)
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
        private readonly SystemTextJsonFormatter formatter;

        internal PipeWriterConverter(SystemTextJsonFormatter formatter)
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
        private readonly SystemTextJsonFormatter formatter;

        internal StreamConverter(SystemTextJsonFormatter formatter)
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

    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
    private class ExceptionConverter : JsonConverter<Exception>
    {
        /// <summary>
        /// Tracks recursion count while serializing or deserializing an exception.
        /// </summary>
        private static ThreadLocal<int> exceptionRecursionCounter = new();

        private readonly SystemTextJsonFormatter formatter;

        internal ExceptionConverter(SystemTextJsonFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override bool CanConvert(Type typeToConvert) => typeof(Exception).IsAssignableFrom(typeToConvert);

        public override Exception? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Assumes.NotNull(this.formatter.JsonRpc);

            exceptionRecursionCounter.Value++;
            try
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new InvalidOperationException("Expected a StartObject token.");
                }

                if (exceptionRecursionCounter.Value > this.formatter.JsonRpc.ExceptionOptions.RecursionLimit)
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

                return ExceptionSerializationHelpers.Deserialize<Exception>(this.formatter.JsonRpc, info, this.formatter.JsonRpc?.TraceSource);
            }
            finally
            {
                exceptionRecursionCounter.Value--;
            }
        }

        public override void Write(Utf8JsonWriter writer, Exception value, JsonSerializerOptions options)
        {
            // We have to guard our own recursion because the serializer has no visibility into inner exceptions.
            // Each exception in the russian doll is a new serialization job from its perspective.
            exceptionRecursionCounter.Value++;
            try
            {
                if (exceptionRecursionCounter.Value > this.formatter.JsonRpc?.ExceptionOptions.RecursionLimit)
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
                    JsonSerializer.Serialize(writer, element.Value, options);
                }

                writer.WriteEndObject();
            }
            catch (Exception ex)
            {
                throw new JsonException(ex.Message, ex);
            }
            finally
            {
                exceptionRecursionCounter.Value--;
            }
        }
    }

    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
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

            return jsonValue.Deserialize(type, this.serializerOptions)!;
        }

        public object Convert(object value, TypeCode typeCode)
        {
            return typeCode switch
            {
                TypeCode.Object => ((JsonNode)value).Deserialize(typeof(object), this.serializerOptions)!,
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

    /// <summary>
    /// Adds compatibility with DataContractSerializer attributes.
    /// </summary>
    /// <example>
    /// To enable this resolver, add the following when creating your <see cref="SystemTextJsonFormatter"/> instance:
    /// <code><![CDATA[
    /// var formatter = new SystemTextJsonFormatter
    /// {
    ///     JsonSerializerOptions =
    ///     {
    ///         TypeInfoResolver = new SystemTextJsonFormatter.DataContractResolver(),
    ///     },
    /// };
    /// ]]></code>
    /// </example>
    [RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
    private class DataContractResolver : IJsonTypeInfoResolver
    {
        private readonly ConcurrentDictionary<Type, JsonTypeInfo?> typeInfoCache = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="DataContractResolver"/> class.
        /// </summary>
        public DataContractResolver()
        {
        }

        /// <summary>
        /// Gets the fallback resolver to use for types lacking a <see cref="DataContractAttribute"/>.
        /// </summary>
        /// <value>The default value is an instance of <see cref="DefaultJsonTypeInfoResolver"/>.</value>
        public IJsonTypeInfoResolver FallbackResolver { get; init; } = new DefaultJsonTypeInfoResolver();

        /// <inheritdoc/>
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (!this.typeInfoCache.TryGetValue(type, out JsonTypeInfo? typeInfo))
            {
                DataContractAttribute? dataContractAttribute = type.GetCustomAttribute<DataContractAttribute>();
                if (dataContractAttribute is not null)
                {
                    // PERF: Consider using the generic CreateJsonTypeInfo method to avoid boxing.
                    // But even so, the JsonPropertyInfo<T> generic type is internal, so we can't avoid boxing of property/field values.
                    typeInfo = JsonTypeInfo.CreateJsonTypeInfo(type, options);
                    typeInfo.CreateObject = () => FormatterServices.GetUninitializedObject(type);
                    PopulateMembersInfos(type, typeInfo, dataContractAttribute);
                }
                else
                {
                    typeInfo = this.FallbackResolver.GetTypeInfo(type, options);
                }

                this.typeInfoCache.TryAdd(type, typeInfo);
            }

            return typeInfo;
        }

        private static void PopulateMembersInfos(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicProperties | DynamicallyAccessedMemberTypes.PublicProperties)] Type type,
            JsonTypeInfo jsonTypeInfo,
            DataContractAttribute? dataContractAttribute)
        {
            BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.Instance;

            // When the type is decorated with DataContractAttribute, we can consider non-public members.
            if (dataContractAttribute is not null)
            {
                bindingFlags |= BindingFlags.NonPublic;
            }

            foreach (PropertyInfo propertyInfo in type.GetProperties(bindingFlags))
            {
                if (TryCreateJsonPropertyInfo(propertyInfo, propertyInfo.PropertyType, out JsonPropertyInfo? jsonPropertyInfo))
                {
                    if (propertyInfo.CanRead)
                    {
                        jsonPropertyInfo.Get = propertyInfo.GetValue;
                    }

                    if (propertyInfo.CanWrite)
                    {
                        jsonPropertyInfo.Set = propertyInfo.SetValue;
                    }
                }
            }

            foreach (FieldInfo fieldInfo in type.GetFields(bindingFlags))
            {
                if (TryCreateJsonPropertyInfo(fieldInfo, fieldInfo.FieldType, out JsonPropertyInfo? jsonPropertyInfo))
                {
                    jsonPropertyInfo.Get = fieldInfo.GetValue;
                    if (!fieldInfo.IsInitOnly)
                    {
                        jsonPropertyInfo.Set = fieldInfo.SetValue;
                    }
                }
            }

            bool TryCreateJsonPropertyInfo(
                MemberInfo memberInfo,
                [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type propertyType,
                [NotNullWhen(true)] out JsonPropertyInfo? jsonPropertyInfo)
            {
                DataMemberAttribute? dataMemberAttribute = memberInfo.GetCustomAttribute<DataMemberAttribute>();
                if ((dataContractAttribute is null || dataMemberAttribute is not null) && memberInfo.GetCustomAttribute<IgnoreDataMemberAttribute>() is null)
                {
                    jsonPropertyInfo = jsonTypeInfo.CreateJsonPropertyInfo(propertyType, dataMemberAttribute?.Name ?? memberInfo.Name);
                    if (dataMemberAttribute is not null)
                    {
                        jsonPropertyInfo.Order = dataMemberAttribute.Order;
                        jsonPropertyInfo.IsRequired = dataMemberAttribute.IsRequired;
                        if (!dataMemberAttribute.EmitDefaultValue)
                        {
                            object? defaultValue = propertyType.IsValueType ? FormatterServices.GetUninitializedObject(propertyType) : null;
                            jsonPropertyInfo.ShouldSerialize = (_, value) => !object.Equals(defaultValue, value);
                        }
                    }

                    jsonTypeInfo.Properties.Add(jsonPropertyInfo);
                    return true;
                }

                jsonPropertyInfo = null;
                return false;
            }
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

    [JsonSerializable(typeof(RequestId))]
    private partial class SourceGenerationContext : JsonSerializerContext;
}
