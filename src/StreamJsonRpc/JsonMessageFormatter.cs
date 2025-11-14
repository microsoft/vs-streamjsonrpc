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
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Uses Newtonsoft.Json serialization to serialize <see cref="JsonRpcMessage"/> as JSON (text).
/// </summary>
/// <remarks>
/// Each instance of this class may only be used with a single <see cref="JsonRpc" /> instance.
/// </remarks>
[RequiresDynamicCode(RuntimeReasons.Formatters), RequiresUnreferencedCode(RuntimeReasons.Formatters)]
public class JsonMessageFormatter : FormatterBase, IJsonRpcAsyncMessageTextFormatter, IJsonRpcMessageFactory
{
    /// <summary>
    /// The key into an <see cref="Exception.Data"/> dictionary whose value may be a <see cref="JToken"/> that failed deserialization.
    /// </summary>
    internal const string ExceptionDataKey = "JToken";

    private static readonly ProxyFactory ProxyFactory = ProxyFactory.Default;

    /// <summary>
    /// JSON parse settings.
    /// </summary>
    /// <remarks>
    /// We save MBs of memory by turning off line info handling.
    /// </remarks>
    private static readonly JsonLoadSettings LoadSettings = new JsonLoadSettings { LineInfoHandling = LineInfoHandling.Ignore };

    /// <summary>
    /// A collection of supported protocol versions.
    /// </summary>
    private static readonly IReadOnlyCollection<Version> SupportedProtocolVersions = new Version[] { new Version(1, 0), new Version(2, 0) };

    /// <summary>
    /// UTF-8 encoding without a preamble.
    /// </summary>
    private static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The <see cref="char"/> array pool to use for each <see cref="JsonTextReader"/> instance.
    /// </summary>
    private static readonly IArrayPool<char> JsonCharArrayPool = new JsonArrayPool<char>(ArrayPool<char>.Shared);

    /// <summary>
    /// An exactly default instance of the <see cref="JsonSerializer"/> to use where no special settings
    /// are needed.
    /// </summary>
    /// <remarks>
    /// This is useful when calling such APIs as <see cref="JToken.FromObject(object, JsonSerializer)"/>
    /// to avoid the simpler overloads that rely on <see cref="JsonConvert.DefaultSettings"/> which is a mutable static.
    /// By sharing this reliably untainted instance, we avoid allocating a new serializer with each invocation.
    /// </remarks>
    private static readonly JsonSerializer DefaultSerializer = JsonSerializer.Create();

    /// <summary>
    /// The reusable <see cref="TextWriter"/> to use with newtonsoft.json's serializer.
    /// </summary>
    private readonly BufferTextWriter bufferTextWriter = new BufferTextWriter();

    /// <summary>
    /// The reusable <see cref="TextReader"/> to use with newtonsoft.json's deserializer.
    /// </summary>
    private readonly SequenceTextReader sequenceTextReader = new SequenceTextReader();

    /// <summary>
    /// Object used to lock when running mutually exclusive operations related to this <see cref="JsonMessageFormatter"/> instance.
    /// </summary>
    private readonly object syncObject = new();

    /// <summary>
    /// A value indicating whether a request where <see cref="Protocol.JsonRpcRequest.RequestId"/> is a <see cref="string"/>
    /// has been transmitted.
    /// </summary>
    /// <remarks>
    /// This is useful to detect whether <see cref="JsonRpc"/> is operating in its default mode of producing
    /// integer-based values for <see cref="Protocol.JsonRpcRequest.RequestId"/>, which informs us whether we should
    /// type coerce strings in response messages back to integers to accommodate JSON-RPC servers
    /// that improperly convert our integers to strings.
    /// </remarks>
    private bool observedTransmittedRequestWithStringId;

    /// <summary>
    /// The version of the JSON-RPC protocol being emulated by this instance.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private Version protocolVersion = new Version(2, 0);

    /// <summary>
    /// Backing field for the <see cref="Encoding"/> property.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private Encoding encoding;

    /// <summary>
    /// Whether <see cref="EnforceFormatterIsInitialized"/> has been executed.
    /// </summary>
    private bool formatterInitializationChecked;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonMessageFormatter"/> class
    /// that uses JsonProgress (without the preamble) for its text encoding.
    /// </summary>
    public JsonMessageFormatter()
        : this(DefaultEncoding)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonMessageFormatter"/> class.
    /// </summary>
    /// <param name="encoding">The encoding to use for the JSON text.</param>
    public JsonMessageFormatter(Encoding encoding)
    {
        Requires.NotNull(encoding, nameof(encoding));
        this.encoding = encoding;

        this.JsonSerializer = new JsonSerializer()
        {
            NullValueHandling = NullValueHandling.Ignore,
            ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
            DateParseHandling = DateParseHandling.None,
            ContractResolver = new MarshalContractResolver(this),
            Converters =
            {
                new JsonProgressServerConverter(this),
                new JsonProgressClientConverter(this),
                new AsyncEnumerableConsumerConverter(this),
                new AsyncEnumerableGeneratorConverter(this),
                new DuplexPipeConverter(this),
                new PipeReaderConverter(this),
                new PipeWriterConverter(this),
                new StreamConverter(this),
                new ExceptionConverter(this),
            },
        };
    }

    /// <summary>
    /// Gets or sets the encoding to use for transmitted messages.
    /// </summary>
    public Encoding Encoding
    {
        get => this.encoding;

        set
        {
            Requires.NotNull(value, nameof(value));
            this.encoding = value;
        }
    }

    /// <summary>
    /// Gets or sets the version of the JSON-RPC protocol emulated by this instance.
    /// </summary>
    /// <value>The default value is 2.0.</value>
    public Version ProtocolVersion
    {
        get => this.protocolVersion;

        set
        {
            Requires.NotNull(value, nameof(value));
            if (!SupportedProtocolVersions.Contains(value))
            {
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Resources.UnsupportedJsonRpcProtocolVersion, value, string.Join(", ", SupportedProtocolVersions)));
            }

            this.protocolVersion = value;
        }
    }

    /// <summary>
    /// Gets the <see cref="Newtonsoft.Json.JsonSerializer"/> used when serializing and deserializing method arguments and return values.
    /// </summary>
    public JsonSerializer JsonSerializer { get; }

    /// <inheritdoc cref="FormatterBase.MultiplexingStream" />
    public new MultiplexingStream? MultiplexingStream
    {
        get => base.MultiplexingStream;
        set => base.MultiplexingStream = value;
    }

    /// <inheritdoc/>
    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer) => this.Deserialize(contentBuffer, this.Encoding);

    /// <inheritdoc/>
    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer, Encoding encoding)
    {
        Requires.NotNull(encoding, nameof(encoding));

        JToken json = this.ReadJToken(contentBuffer, encoding);
        JsonRpcMessage message = this.Deserialize(json);

        IJsonRpcTracingCallbacks? tracingCallbacks = this.JsonRpc;
        tracingCallbacks?.OnMessageDeserialized(message, json);

        return message;
    }

    /// <inheritdoc/>
    public async ValueTask<JsonRpcMessage> DeserializeAsync(PipeReader reader, Encoding encoding, CancellationToken cancellationToken)
    {
        Requires.NotNull(reader, nameof(reader));
        Requires.NotNull(encoding, nameof(encoding));

        using (var jsonReader = new JsonTextReader(new StreamReader(reader.AsStream(), encoding)))
        {
            this.ConfigureJsonTextReader(jsonReader);
            JToken json = await JToken.ReadFromAsync(jsonReader, LoadSettings, cancellationToken).ConfigureAwait(false);
            JsonRpcMessage message = this.Deserialize(json);

            IJsonRpcTracingCallbacks? tracingCallbacks = this.JsonRpc;
            tracingCallbacks?.OnMessageDeserialized(message, json);

            return message;
        }
    }

    /// <inheritdoc/>
    public ValueTask<JsonRpcMessage> DeserializeAsync(PipeReader reader, CancellationToken cancellationToken) => this.DeserializeAsync(reader, this.Encoding, cancellationToken);

    /// <inheritdoc/>
    public void Serialize(IBufferWriter<byte> contentBuffer, JsonRpcMessage message)
    {
        JToken json = this.Serialize(message);

        IJsonRpcTracingCallbacks? tracingCallbacks = this.JsonRpc;
        tracingCallbacks?.OnMessageSerialized(message, json);

        this.WriteJToken(contentBuffer, json);
    }

    /// <summary>
    /// Deserializes a <see cref="JToken"/> to a <see cref="JsonRpcMessage"/>.
    /// </summary>
    /// <param name="json">The JSON to deserialize.</param>
    /// <returns>The deserialized message.</returns>
    public JsonRpcMessage Deserialize(JToken json)
    {
        Requires.NotNull(json, nameof(json));

        this.EnforceFormatterIsInitialized();

        try
        {
            switch (this.ProtocolVersion.Major)
            {
                case 1:
                    this.VerifyProtocolCompliance(json["jsonrpc"] is null, json, "\"jsonrpc\" property not expected. Use protocol version 2.0.");
                    this.VerifyProtocolCompliance(json["id"] is not null, json, "\"id\" property missing.");
                    return
                        json["method"] is not null ? this.ReadRequest(json) :
                        json["error"]?.Type == JTokenType.Null ? this.ReadResult(json) :
                        json["error"] is { Type: not JTokenType.Null } ? this.ReadError(json) :
                        throw this.CreateProtocolNonComplianceException(json);
                case 2:
                    this.VerifyProtocolCompliance(json.Value<string>("jsonrpc") == "2.0", json, $"\"jsonrpc\" property must be set to \"2.0\", or set {nameof(this.ProtocolVersion)} to 1.0 mode.");
                    return
                        json["method"] is not null ? this.ReadRequest(json) :
                        json["result"] is not null ? this.ReadResult(json) :
                        json["error"] is not null ? this.ReadError(json) :
                        throw this.CreateProtocolNonComplianceException(json);
                default:
                    throw Assumes.NotReachable();
            }
        }
        catch (JsonException exception)
        {
            var serializationException = new JsonSerializationException($"Unable to deserialize {nameof(JsonRpcMessage)}.", exception);
            if (json.GetType().GetTypeInfo().IsSerializable)
            {
                serializationException.Data[ExceptionDataKey] = json;
            }

            throw serializationException;
        }
    }

    /// <summary>
    /// Serializes a <see cref="JsonRpcMessage"/> to a <see cref="JToken"/>.
    /// </summary>
    /// <param name="message">The message to serialize.</param>
    /// <returns>The JSON of the message.</returns>
    public JToken Serialize(JsonRpcMessage message)
    {
        this.EnforceFormatterIsInitialized();

        try
        {
            this.observedTransmittedRequestWithStringId |= message is JsonRpcRequest request && request.RequestId.String is not null;

            // Pre-tokenize the user data so we can use their custom converters for just their data and not for the base message.
            this.TokenizeUserData(message);

            var json = JToken.FromObject(message, DefaultSerializer);

            // Fix up dropped fields that are mandatory
            if (message is Protocol.JsonRpcResult && json["result"] is null)
            {
                json["result"] = JValue.CreateNull();
            }

            if (this.ProtocolVersion.Major == 1 && json["id"] is null)
            {
                // JSON-RPC 1.0 requires the id property to be present even for notifications.
                json["id"] = JValue.CreateNull();
            }

            // Copy over extra top-level properties.
            if (message is IMessageWithTopLevelPropertyBag { TopLevelPropertyBag: TopLevelPropertyBag bag })
            {
                bag.WriteProperties(json);
            }

            return json;
        }
        catch (Exception ex)
        {
            throw new JsonSerializationException(string.Format(CultureInfo.CurrentCulture, Resources.ErrorWritingJsonRpcMessage, ex.GetType().Name, ex.Message), ex);
        }
    }

    /// <inheritdoc/>
    /// <devremarks>
    /// Do *NOT* change this to simply forward to <see cref="Serialize(JsonRpcMessage)"/> since that
    /// mutates the <see cref="JsonRpcMessage"/> itself by tokenizing arguments as if we were going to transmit them
    /// which BREAKS argument parsing for incoming named argument messages such as $/cancelRequest.
    /// </devremarks>
    public object GetJsonText(JsonRpcMessage message) => throw new NotSupportedException();

    /// <inheritdoc/>
    Protocol.JsonRpcRequest IJsonRpcMessageFactory.CreateRequestMessage() => new OutboundJsonRpcRequest(this);

    /// <inheritdoc/>
    Protocol.JsonRpcError IJsonRpcMessageFactory.CreateErrorMessage() => new JsonRpcError(this.JsonSerializer);

    /// <inheritdoc/>
    Protocol.JsonRpcResult IJsonRpcMessageFactory.CreateResultMessage() => new JsonRpcResult(this);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.sequenceTextReader.Dispose();
            this.bufferTextWriter.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    private protected override MessageFormatterRpcMarshaledContextTracker CreateMessageFormatterRpcMarshaledContextTracker(JsonRpc rpc) => new MessageFormatterRpcMarshaledContextTracker.Dynamic(rpc, ProxyFactory, this);

    private static IReadOnlyDictionary<string, object> PartiallyParseNamedArguments(JObject args)
    {
        Requires.NotNull(args, nameof(args));

        return args.Properties().ToDictionary(p => p.Name, p => (object)p.Value);
    }

    private static object[] PartiallyParsePositionalArguments(JArray args)
    {
        Requires.NotNull(args, nameof(args));

        var jtokenArray = new JToken[args.Count];
        for (int i = 0; i < jtokenArray.Length; i++)
        {
            jtokenArray[i] = args[i];
        }

        return jtokenArray;
    }

    private void EnforceFormatterIsInitialized()
    {
        lock (this.syncObject)
        {
            if (!this.formatterInitializationChecked)
            {
                IContractResolver? originalContractResolver = this.JsonSerializer.ContractResolver;
                if (originalContractResolver is not MarshalContractResolver)
                {
                    this.JsonSerializer.ContractResolver = new MarshalContractResolver(this, originalContractResolver);
                }

                this.formatterInitializationChecked = true;
            }
        }
    }

    private void VerifyProtocolCompliance(bool condition, JToken message, string? explanation = null)
    {
        if (!condition)
        {
            throw this.CreateProtocolNonComplianceException(message, explanation);
        }
    }

    private Exception CreateProtocolNonComplianceException(JToken message, string? explanation = null)
    {
        var builder = new StringBuilder();
        builder.AppendFormat(CultureInfo.CurrentCulture, "Unrecognized JSON-RPC {0} message", this.ProtocolVersion);
        if (explanation is not null)
        {
            builder.AppendFormat(CultureInfo.CurrentCulture, " ({0})", explanation);
        }

        builder.Append(": ");
        builder.Append(message);
        return new JsonSerializationException(builder.ToString());
    }

    private void WriteJToken(IBufferWriter<byte> contentBuffer, JToken json)
    {
        this.bufferTextWriter.Initialize(contentBuffer, this.Encoding);
        using (var jsonWriter = new JsonTextWriter(this.bufferTextWriter))
        {
            try
            {
                json.WriteTo(jsonWriter);
            }
            finally
            {
                jsonWriter.Flush();
            }
        }
    }

    private JToken ReadJToken(ReadOnlySequence<byte> contentBuffer, Encoding encoding)
    {
        Requires.NotNull(encoding, nameof(encoding));

        this.sequenceTextReader.Initialize(contentBuffer, encoding);
        using (var jsonReader = new JsonTextReader(this.sequenceTextReader))
        {
            this.ConfigureJsonTextReader(jsonReader);
            JToken json = JToken.ReadFrom(jsonReader, LoadSettings);
            return json;
        }
    }

    private void ConfigureJsonTextReader(JsonTextReader reader)
    {
        Requires.NotNull(reader, nameof(reader));

        reader.ArrayPool = JsonCharArrayPool;
        reader.CloseInput = true;
        reader.Culture = this.JsonSerializer.Culture;
        reader.DateFormatString = this.JsonSerializer.DateFormatString;
        reader.DateParseHandling = this.JsonSerializer.DateParseHandling;
        reader.DateTimeZoneHandling = this.JsonSerializer.DateTimeZoneHandling;
        reader.FloatParseHandling = this.JsonSerializer.FloatParseHandling;
        reader.MaxDepth = this.JsonSerializer.MaxDepth;
    }

    /// <summary>
    /// Converts user data to <see cref="JToken"/> objects using all applicable user-provided <see cref="JsonConverter"/> instances.
    /// </summary>
    /// <param name="jsonRpcMessage">A JSON-RPC message.</param>
    private void TokenizeUserData(JsonRpcMessage jsonRpcMessage)
    {
        using (this.TrackSerialization(jsonRpcMessage))
        {
            switch (jsonRpcMessage)
            {
                case Protocol.JsonRpcRequest request:
                    if (request.ArgumentsList is not null)
                    {
                        var tokenizedArgumentsList = new JToken[request.ArgumentsList.Count];
                        for (int i = 0; i < request.ArgumentsList.Count; i++)
                        {
                            tokenizedArgumentsList[i] = this.TokenizeUserData(request.ArgumentListDeclaredTypes?[i], request.ArgumentsList[i]);
                        }

                        request.ArgumentsList = tokenizedArgumentsList;
                    }
                    else if (request.Arguments is not null)
                    {
                        if (this.ProtocolVersion.Major < 2)
                        {
                            throw new NotSupportedException(Resources.ParameterObjectsNotSupportedInJsonRpc10);
                        }

                        // Tokenize the user data using the user-supplied serializer.
                        JObject? paramsObject = JObject.FromObject(request.Arguments, this.JsonSerializer);

                        // Json.Net TypeHandling could insert a $type child JToken to the paramsObject above.
                        // This $type JToken should not be there to maintain Json RPC Spec compatibility. We will
                        // strip the token out here.
                        if (this.JsonSerializer.TypeNameHandling != TypeNameHandling.None)
                        {
                            paramsObject.Remove("$type");
                        }

                        request.Arguments = paramsObject;
                    }

                    break;
                case Protocol.JsonRpcResult result:
                    result.Result = this.TokenizeUserData(result.ResultDeclaredType, result.Result);
                    break;
                case Protocol.JsonRpcError error when error.Error is object:
                    error.Error.Data = error.Error.Data is object ? this.TokenizeUserData(typeof(object), error.Error.Data) : null;
                    break;
            }
        }
    }

    /// <summary>
    /// Converts a single user data value to a <see cref="JToken"/>, using all applicable user-provided <see cref="JsonConverter"/> instances.
    /// </summary>
    /// <param name="declaredType">The <see cref="Type"/> that the <paramref name="value"/> is declared to be, if available.</param>
    /// <param name="value">The value to tokenize.</param>
    /// <returns>The <see cref="JToken"/> instance.</returns>
    private JToken TokenizeUserData(Type? declaredType, object? value)
    {
        if (value is JToken token)
        {
            return token;
        }

        if (value is null)
        {
            return JValue.CreateNull();
        }

        if (declaredType is not null && this.TryGetMarshaledJsonConverter(declaredType, out RpcMarshalableConverter? converter))
        {
            using JTokenWriter jsonWriter = this.CreateJTokenWriter();
            converter.WriteJson(jsonWriter, value, this.JsonSerializer);
            return jsonWriter.Token!;
        }

        return JToken.FromObject(value, this.JsonSerializer);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JTokenWriter"/> class
    /// with settings initialized to those set on the <see cref="JsonSerializer"/> object.
    /// </summary>
    /// <returns>The initialized instance of <see cref="JTokenWriter"/>.</returns>
    private JTokenWriter CreateJTokenWriter()
    {
        return new JTokenWriter
        {
            // This same set of properties comes from Newtonsoft.Json's own JsonSerialize.SerializeInternal method.
            Formatting = this.JsonSerializer.Formatting,
            DateFormatHandling = this.JsonSerializer.DateFormatHandling,
            DateTimeZoneHandling = this.JsonSerializer.DateTimeZoneHandling,
            FloatFormatHandling = this.JsonSerializer.FloatFormatHandling,
            StringEscapeHandling = this.JsonSerializer.StringEscapeHandling,
            Culture = this.JsonSerializer.Culture,
            DateFormatString = this.JsonSerializer.DateFormatString,
        };
    }

    private bool TryGetMarshaledJsonConverter(Type type, [NotNullWhen(true)] out RpcMarshalableConverter? converter)
    {
        if (MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(type, JsonRpcProxyOptions.Default, out JsonRpcProxyOptions? proxyOptions, out JsonRpcTargetOptions? targetOptions, out RpcMarshalableAttribute? rpcMarshalableAttribute))
        {
            converter = new RpcMarshalableConverter(type, this, proxyOptions, targetOptions, rpcMarshalableAttribute);
            return true;
        }

        converter = null;
        return false;
    }

    private InboundJsonRpcRequest ReadRequest(JToken json)
    {
        Requires.NotNull(json, nameof(json));

        RequestId id = json["id"]?.ToObject<RequestId>(DefaultSerializer) ?? default;

        // We leave arguments as JTokens at this point, so that we can try deserializing them
        // to more precise .NET types as required by the method we're invoking.
        JToken? args = json["params"];
        object? arguments =
            args is JObject argsObject ? PartiallyParseNamedArguments(argsObject) :
            args is JArray argsArray ? (object)PartiallyParsePositionalArguments(argsArray) :
            null;

        InboundJsonRpcRequest request = new(this)
        {
            RequestId = id,
            Method = json.Value<string>("method"),
            Arguments = arguments,
            TraceParent = json.Value<string>("traceparent"),
            TraceState = json.Value<string>("tracestate"),
            TopLevelPropertyBag = new TopLevelPropertyBag(this.JsonSerializer, (JObject)json),
        };

        this.TryHandleSpecialIncomingMessage(request);

        return request;
    }

    private JsonRpcResult ReadResult(JToken json)
    {
        Requires.NotNull(json, nameof(json));

        RequestId id = this.ExtractRequestId(json);
        JToken? result = json["result"];

        return new JsonRpcResult(this)
        {
            RequestId = id,
            Result = result,
            TopLevelPropertyBag = new TopLevelPropertyBag(this.JsonSerializer, (JObject)json),
        };
    }

    private JsonRpcError ReadError(JToken json)
    {
        Requires.NotNull(json, nameof(json));

        RequestId id = this.ExtractRequestId(json);
        JToken? error = json["error"];
        Assumes.NotNull(error); // callers should have verified this already.

        return new JsonRpcError(this.JsonSerializer)
        {
            RequestId = id,
            Error = new ErrorDetail(this.JsonSerializer)
            {
                Code = (JsonRpcErrorCode)error.Value<long>("code"),
                Message = error.Value<string>("message"),
                Data = error["data"], // leave this as a JToken. We deserialize inside GetData<T>
            },
            TopLevelPropertyBag = new TopLevelPropertyBag(this.JsonSerializer, (JObject)json),
        };
    }

    private RequestId ExtractRequestId(JToken json)
    {
        JToken? idToken = json["id"];
        if (idToken is null)
        {
            throw this.CreateProtocolNonComplianceException(json, "\"id\" property missing.");
        }

        RequestId id = this.NormalizeId(idToken.ToObject<RequestId>(DefaultSerializer));
        return id;
    }

    private RequestId NormalizeId(RequestId id)
    {
        if (!this.observedTransmittedRequestWithStringId && id.String is not null && long.TryParse(id.String, out long idAsNumber))
        {
            id = new RequestId(idAsNumber);
        }

        return id;
    }

    private class TopLevelPropertyBag : TopLevelPropertyBagBase
    {
        private readonly JsonSerializer jsonSerializer;

        /// <summary>
        /// The incoming message.
        /// </summary>
        private JObject? incomingEnvelope;

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for use with an incoming message.
        /// </summary>
        /// <param name="jsonSerializer">The serializer to use.</param>
        /// <param name="incomingMessage">The incoming message.</param>
        internal TopLevelPropertyBag(JsonSerializer jsonSerializer, JObject incomingMessage)
            : base(isOutbound: false)
        {
            this.jsonSerializer = jsonSerializer;
            this.incomingEnvelope = incomingMessage;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for use with an outcoming message.
        /// </summary>
        /// <param name="jsonSerializer">The serializer to use.</param>
        internal TopLevelPropertyBag(JsonSerializer jsonSerializer)
            : base(isOutbound: true)
        {
            this.jsonSerializer = jsonSerializer;
        }

        internal void WriteProperties(JToken envelope)
        {
            if (this.incomingEnvelope is not null)
            {
                // We're actually re-transmitting an incoming message (remote target feature).
                // We need to copy all the properties that were in the original message.
                foreach (JProperty property in this.incomingEnvelope.Properties())
                {
                    if (!Constants.Request.IsPropertyReserved(property.Name) && envelope[property.Name] is null)
                    {
                        envelope[property.Name] = property.Value;
                    }
                }
            }
            else
            {
                foreach (KeyValuePair<string, (Type DeclaredType, object? Value)> property in this.OutboundProperties)
                {
                    if (envelope[property.Key] is null)
                    {
                        envelope[property.Key] = property.Value.Value is null ? JValue.CreateNull() : JToken.FromObject(property.Value.Value, this.jsonSerializer);
                    }
                }
            }
        }

        protected internal override bool TryGetTopLevelProperty<T>(string name, [MaybeNull] out T value)
        {
            if (this.incomingEnvelope!.TryGetValue(name, out JToken? serializedValue) is true)
            {
                value = serializedValue is null ? default : serializedValue.ToObject<T>(this.jsonSerializer);
                return true;
            }

            value = default;
            return false;
        }
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    [DataContract]
    private class OutboundJsonRpcRequest : JsonRpcRequestBase
    {
        private readonly JsonMessageFormatter formatter;

        internal OutboundJsonRpcRequest(JsonMessageFormatter formatter)
        {
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatter.JsonSerializer);
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    [DataContract]
    private class InboundJsonRpcRequest : JsonRpcRequestBase
    {
        private readonly JsonMessageFormatter formatter;

        internal InboundJsonRpcRequest(JsonMessageFormatter formatter)
        {
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        }

        public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, Span<object?> typedArguments)
        {
            using (this.formatter.TrackDeserialization(this, parameters))
            {
                if (parameters.Length == 1 && this.NamedArguments is not null)
                {
                    // Special support for accepting a single JToken instead of all parameters individually.
                    if (parameters[0].ParameterType == typeof(JToken))
                    {
                        var obj = new JObject();
                        foreach (KeyValuePair<string, object?> property in this.NamedArguments)
                        {
                            obj.Add(new JProperty(property.Key, property.Value));
                        }

                        typedArguments[0] = obj;
                        return ArgumentMatchResult.Success;
                    }

                    // Support for opt-in to deserializing all named arguments into a single parameter.
                    if (this.formatter.ApplicableMethodAttributeOnDeserializingMethod?.UseSingleObjectParameterDeserialization is true)
                    {
                        var obj = new JObject();
                        foreach (KeyValuePair<string, object?> property in this.NamedArguments)
                        {
                            obj.Add(new JProperty(property.Key, property.Value));
                        }

                        typedArguments[0] = obj.ToObject(parameters[0].ParameterType, this.formatter.JsonSerializer);

                        return ArgumentMatchResult.Success;
                    }
                }

                return base.TryGetTypedArguments(parameters, typedArguments);
            }
        }

        public override bool TryGetArgumentByNameOrIndex(string? name, int position, Type? typeHint, out object? value)
        {
            if (base.TryGetArgumentByNameOrIndex(name, position, typeHint, out value))
            {
                var token = (JToken?)value;
                try
                {
                    using (this.formatter.TrackDeserialization(this))
                    {
                        value = token?.ToObject(typeHint!, this.formatter.JsonSerializer); // Null typeHint is allowed (https://github.com/JamesNK/Newtonsoft.Json/pull/2562)
                    }

                    return true;
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

            return false;
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatter.JsonSerializer);
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    [DataContract]
    private class JsonRpcResult : JsonRpcResultBase
    {
        private readonly JsonMessageFormatter formatter;
        private bool resultDeserialized;
        private JsonSerializationException? resultDeserializationException;

        internal JsonRpcResult(JsonMessageFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override T GetResult<T>()
        {
            if (this.resultDeserializationException is not null)
            {
                ExceptionDispatchInfo.Capture(this.resultDeserializationException).Throw();
            }

            if (this.resultDeserialized)
            {
                return (T)this.Result!;
            }

            Verify.Operation(this.Result is not null, "This instance hasn't been initialized with a result yet.");
            var result = (JToken)this.Result;
            if (result.Type == JTokenType.Null)
            {
                Verify.Operation(!typeof(T).GetTypeInfo().IsValueType || Nullable.GetUnderlyingType(typeof(T)) is not null, "null result is not assignable to a value type.");
                return default!;
            }

            try
            {
                using (this.formatter.TrackDeserialization(this))
                {
                    return result.ToObject<T>(this.formatter.JsonSerializer)!;
                }
            }
            catch (Exception exception)
            {
                throw new JsonSerializationException(string.Format(CultureInfo.CurrentCulture, Resources.FailureDeserializingRpcResult, typeof(T).Name, exception.GetType().Name, exception.Message), exception);
            }
        }

        protected internal override void SetExpectedResultType(Type resultType)
        {
            Verify.Operation(this.Result is not null, "This instance hasn't been initialized with a result yet.");
            Verify.Operation(!this.resultDeserialized, "Result is no longer available or has already been deserialized.");

            var result = (JToken)this.Result;
            if (result.Type == JTokenType.Null)
            {
                Verify.Operation(!resultType.GetTypeInfo().IsValueType || Nullable.GetUnderlyingType(resultType) is not null, "null result is not assignable to a value type.");
                return;
            }

            try
            {
                using (this.formatter.TrackDeserialization(this))
                {
                    this.Result = result.ToObject(resultType, this.formatter.JsonSerializer)!;
                    this.resultDeserialized = true;
                }
            }
            catch (Exception exception)
            {
                // This was a best effort anyway. We'll throw again later at a more convenient time for JsonRpc.
                this.resultDeserializationException = new JsonSerializationException(string.Format(CultureInfo.CurrentCulture, Resources.FailureDeserializingRpcResult, resultType.Name, exception.GetType().Name, exception.Message), exception);
            }
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatter.JsonSerializer);
    }

    private class JsonRpcError : JsonRpcErrorBase
    {
        private readonly JsonSerializer jsonSerializer;

        internal JsonRpcError(JsonSerializer jsonSerializer)
        {
            this.jsonSerializer = Requires.NotNull(jsonSerializer, nameof(jsonSerializer));
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.jsonSerializer);
    }

    [DataContract]
    private class ErrorDetail : Protocol.JsonRpcError.ErrorDetail
    {
        private readonly JsonSerializer jsonSerializer;

        internal ErrorDetail(JsonSerializer jsonSerializer)
        {
            this.jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
        }

        public override object? GetData(Type dataType)
        {
            Requires.NotNull(dataType, nameof(dataType));

            var data = (JToken?)this.Data;
            if (data?.Type == JTokenType.Null)
            {
                Verify.Operation(!dataType.GetTypeInfo().IsValueType || Nullable.GetUnderlyingType(dataType) is not null, "null result is not assignable to a value type.");
                return default!;
            }

            try
            {
                return data?.ToObject(dataType, this.jsonSerializer);
            }
            catch (JsonReaderException)
            {
                return data;
            }
            catch (JsonSerializationException)
            {
                return data;
            }
        }
    }

    /// <summary>
    /// Adapts the .NET <see cref="ArrayPool{T}" /> to Newtonsoft.Json's <see cref="IArrayPool{T}" /> interface.
    /// </summary>
    private class JsonArrayPool<T> : IArrayPool<T>
    {
        private readonly ArrayPool<T> arrayPool;

        internal JsonArrayPool(ArrayPool<T> arrayPool)
        {
            this.arrayPool = arrayPool ?? throw new ArgumentNullException(nameof(arrayPool));
        }

        public T[] Rent(int minimumLength) => this.arrayPool.Rent(minimumLength);

        public void Return(T[]? array)
        {
            if (array is object)
            {
                this.arrayPool.Return(array);
            }
        }
    }

    /// <summary>
    /// Converts an instance of <see cref="IProgress{T}"/> to a progress token.
    /// </summary>
    private class JsonProgressClientConverter : JsonConverter
    {
        private readonly JsonMessageFormatter formatter;

        public JsonProgressClientConverter(JsonMessageFormatter formatter)
        {
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        }

        public override bool CanConvert(Type objectType) => MessageFormatterProgressTracker.CanSerialize(objectType);

        public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            long progressId = this.formatter.FormatterProgressTracker.GetTokenForProgress(value!);
            writer.WriteValue(progressId);
        }
    }

    /// <summary>
    /// Converts a progress token to an <see cref="IProgress{T}"/>.
    /// </summary>
    [RequiresDynamicCode(RuntimeReasons.CloseGenerics)]
    private class JsonProgressServerConverter : JsonConverter
    {
        private readonly JsonMessageFormatter formatter;

        public JsonProgressServerConverter(JsonMessageFormatter formatter)
        {
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        }

        public override bool CanConvert(Type objectType) => MessageFormatterProgressTracker.CanDeserialize(objectType);

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            Assumes.NotNull(this.formatter.JsonRpc);
            JToken token = JToken.Load(reader);
            bool clientRequiresNamedArgs = this.formatter.ApplicableMethodAttributeOnDeserializingMethod?.ClientRequiresNamedArguments is true;
            return this.formatter.FormatterProgressTracker.CreateProgress(this.formatter.JsonRpc, token, objectType, clientRequiresNamedArgs);
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Converts an enumeration token to an <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    [RequiresDynamicCode(RuntimeReasons.CloseGenerics)]
    [RequiresUnreferencedCode(RuntimeReasons.CloseGenerics)]
    private class AsyncEnumerableConsumerConverter : JsonConverter
    {
        private static readonly MethodInfo ReadJsonOpenGenericMethod = typeof(AsyncEnumerableConsumerConverter).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Single(m => m.Name == nameof(ReadJson) && m.IsGenericMethod);

        private JsonMessageFormatter formatter;

        internal AsyncEnumerableConsumerConverter(JsonMessageFormatter jsonMessageFormatter)
        {
            this.formatter = jsonMessageFormatter;
        }

        public override bool CanConvert(Type objectType) => MessageFormatterEnumerableTracker.CanDeserialize(objectType);

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            Type? iface = TrackerHelpers.FindIAsyncEnumerableInterfaceImplementedBy(objectType);
            Assumes.NotNull(iface);
            MethodInfo genericMethod = ReadJsonOpenGenericMethod.MakeGenericMethod(iface.GenericTypeArguments[0]);
            try
            {
                return genericMethod.Invoke(this, new object?[] { reader, serializer });
            }
            catch (TargetInvocationException ex)
            {
                if (ex.InnerException is object)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                }

                throw Assumes.NotReachable();
            }
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }

#pragma warning disable VSTHRD200 // Use "Async" suffix in names of methods that return an awaitable type.
        private IAsyncEnumerable<T> ReadJson<T>(JsonReader reader, JsonSerializer serializer)
#pragma warning restore VSTHRD200 // Use "Async" suffix in names of methods that return an awaitable type.
        {
            JToken enumJToken = JToken.Load(reader);
            JToken? handle = enumJToken[MessageFormatterEnumerableTracker.TokenPropertyName];
            IReadOnlyList<T>? prefetchedItems = enumJToken[MessageFormatterEnumerableTracker.ValuesPropertyName]?.ToObject<IReadOnlyList<T>>(serializer);

            return this.formatter.EnumerableTracker.CreateEnumerableProxy(handle, prefetchedItems);
        }
    }

    /// <summary>
    /// Converts an instance of <see cref="IAsyncEnumerable{T}"/> to an enumeration token.
    /// </summary>
    [RequiresDynamicCode(RuntimeReasons.CloseGenerics)]
    [RequiresUnreferencedCode(RuntimeReasons.CloseGenerics)]
    private class AsyncEnumerableGeneratorConverter : JsonConverter
    {
        private static readonly MethodInfo WriteJsonOpenGenericMethod = typeof(AsyncEnumerableGeneratorConverter).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Single(m => m.Name == nameof(WriteJson) && m.IsGenericMethod);

        private JsonMessageFormatter formatter;

        internal AsyncEnumerableGeneratorConverter(JsonMessageFormatter jsonMessageFormatter)
        {
            this.formatter = jsonMessageFormatter;
        }

        public override bool CanConvert(Type objectType) => MessageFormatterEnumerableTracker.CanSerialize(objectType);

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            Type? iface = TrackerHelpers.FindIAsyncEnumerableInterfaceImplementedBy(value!.GetType());
            Assumes.NotNull(iface);
            MethodInfo genericMethod = WriteJsonOpenGenericMethod.MakeGenericMethod(iface.GenericTypeArguments[0]);
            try
            {
                genericMethod.Invoke(this, new object?[] { writer, value, serializer });
            }
            catch (TargetInvocationException ex)
            {
                if (ex.InnerException is object)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                }

                throw Assumes.NotReachable();
            }
        }

        private void WriteJson<T>(JsonWriter writer, IAsyncEnumerable<T> value, JsonSerializer serializer)
        {
            (IReadOnlyList<T> Elements, bool Finished) prefetched = value.TearOffPrefetchedElements();
            long token = this.formatter.EnumerableTracker.GetToken(value);
            writer.WriteStartObject();

            if (!prefetched.Finished)
            {
                writer.WritePropertyName(MessageFormatterEnumerableTracker.TokenPropertyName);
                writer.WriteValue(token);
            }

            if (prefetched.Elements.Count > 0)
            {
                writer.WritePropertyName(MessageFormatterEnumerableTracker.ValuesPropertyName);
                serializer.Serialize(writer, prefetched.Elements);
            }

            writer.WriteEndObject();
        }
    }

    private class DuplexPipeConverter : JsonConverter<IDuplexPipe?>
    {
        private readonly JsonMessageFormatter jsonMessageFormatter;

        public DuplexPipeConverter(JsonMessageFormatter jsonMessageFormatter)
        {
            this.jsonMessageFormatter = jsonMessageFormatter ?? throw new ArgumentNullException(nameof(jsonMessageFormatter));
        }

        public override IDuplexPipe? ReadJson(JsonReader reader, Type objectType, IDuplexPipe? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            ulong? tokenId = JToken.Load(reader).Value<ulong?>();
            return this.jsonMessageFormatter.DuplexPipeTracker!.GetPipe(tokenId);
        }

        public override void WriteJson(JsonWriter writer, IDuplexPipe? value, JsonSerializer serializer)
        {
            ulong? token = this.jsonMessageFormatter.DuplexPipeTracker!.GetULongToken(value);
            writer.WriteValue(token);
        }
    }

    private class PipeReaderConverter : JsonConverter<PipeReader?>
    {
        private readonly JsonMessageFormatter jsonMessageFormatter;

        public PipeReaderConverter(JsonMessageFormatter jsonMessageFormatter)
        {
            this.jsonMessageFormatter = jsonMessageFormatter ?? throw new ArgumentNullException(nameof(jsonMessageFormatter));
        }

        public override PipeReader? ReadJson(JsonReader reader, Type objectType, PipeReader? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            ulong? tokenId = JToken.Load(reader).Value<ulong?>();
            return this.jsonMessageFormatter.DuplexPipeTracker.GetPipeReader(tokenId);
        }

        public override void WriteJson(JsonWriter writer, PipeReader? value, JsonSerializer serializer)
        {
            ulong? token = this.jsonMessageFormatter.DuplexPipeTracker.GetULongToken(value);
            writer.WriteValue(token);
        }
    }

    private class PipeWriterConverter : JsonConverter<PipeWriter?>
    {
        private readonly JsonMessageFormatter jsonMessageFormatter;

        public PipeWriterConverter(JsonMessageFormatter jsonMessageFormatter)
        {
            this.jsonMessageFormatter = jsonMessageFormatter ?? throw new ArgumentNullException(nameof(jsonMessageFormatter));
        }

        public override PipeWriter? ReadJson(JsonReader reader, Type objectType, PipeWriter? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            ulong? tokenId = JToken.Load(reader).Value<ulong?>();
            return this.jsonMessageFormatter.DuplexPipeTracker.GetPipeWriter(tokenId);
        }

        public override void WriteJson(JsonWriter writer, PipeWriter? value, JsonSerializer serializer)
        {
            ulong? token = this.jsonMessageFormatter.DuplexPipeTracker.GetULongToken(value);
            writer.WriteValue(token);
        }
    }

    private class StreamConverter : JsonConverter<Stream?>
    {
        private readonly JsonMessageFormatter jsonMessageFormatter;

        public StreamConverter(JsonMessageFormatter jsonMessageFormatter)
        {
            this.jsonMessageFormatter = jsonMessageFormatter ?? throw new ArgumentNullException(nameof(jsonMessageFormatter));
        }

        public override Stream? ReadJson(JsonReader reader, Type objectType, Stream? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            ulong? tokenId = JToken.Load(reader).Value<ulong?>();
            return this.jsonMessageFormatter.DuplexPipeTracker.GetPipe(tokenId)?.AsStream();
        }

        public override void WriteJson(JsonWriter writer, Stream? value, JsonSerializer serializer)
        {
            ulong? token = this.jsonMessageFormatter.DuplexPipeTracker.GetULongToken(value?.UsePipe());
            writer.WriteValue(token);
        }
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + "}")]
    [RequiresDynamicCode(RuntimeReasons.CloseGenerics)]
    [RequiresUnreferencedCode(RuntimeReasons.RefEmit)]
    private class RpcMarshalableConverter([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.NonPublicEvents | DynamicallyAccessedMemberTypes.Interfaces)] Type interfaceType, JsonMessageFormatter jsonMessageFormatter, JsonRpcProxyOptions proxyOptions, JsonRpcTargetOptions targetOptions, RpcMarshalableAttribute rpcMarshalableAttribute) : JsonConverter
    {
        private string DebuggerDisplay => $"Converter for marshalable objects of type {interfaceType.FullName}";

        public override bool CanConvert(Type objectType) => objectType == interfaceType;

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            var token = (MessageFormatterRpcMarshaledContextTracker.MarshalToken?)JToken.Load(reader).ToObject(typeof(MessageFormatterRpcMarshaledContextTracker.MarshalToken), serializer);
            return jsonMessageFormatter.RpcMarshaledContextTracker.GetObject(objectType, token, proxyOptions);
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is null)
            {
                writer.WriteNull();
            }
            else if (!interfaceType.IsAssignableFrom(value.GetType()))
            {
                throw new InvalidOperationException($"Type {value.GetType().FullName} doesn't implement {interfaceType.FullName}");
            }
            else
            {
                RpcTargetMetadata mapping = RpcTargetMetadata.FromInterface(interfaceType);
                MessageFormatterRpcMarshaledContextTracker.MarshalToken token = jsonMessageFormatter.RpcMarshaledContextTracker.GetToken(value, targetOptions, mapping, rpcMarshalableAttribute);
                serializer.Serialize(writer, token);
            }
        }
    }

    private class JsonConverterFormatter : IFormatterConverter
    {
        private readonly JsonSerializer serializer;

        internal JsonConverterFormatter(JsonSerializer serializer)
        {
            this.serializer = serializer;
        }

#pragma warning disable CS8766 // This method may in fact return null, and no one cares.
        public object? Convert(object value, Type type)
#pragma warning restore CS8766
            => ((JToken)value).ToObject(type, this.serializer);

        public object Convert(object value, TypeCode typeCode)
        {
            return typeCode switch
            {
                TypeCode.Object => ((JToken)value).ToObject(typeof(object), this.serializer)!,
                _ => ExceptionSerializationHelpers.Convert(this, value, typeCode),
            };
        }

        public bool ToBoolean(object value) => ((JToken)value).ToObject<bool>(this.serializer);

        public byte ToByte(object value) => ((JToken)value).ToObject<byte>(this.serializer);

        public char ToChar(object value) => ((JToken)value).ToObject<char>(this.serializer);

        public DateTime ToDateTime(object value) => ((JToken)value).ToObject<DateTime>(this.serializer);

        public decimal ToDecimal(object value) => ((JToken)value).ToObject<decimal>(this.serializer);

        public double ToDouble(object value) => ((JToken)value).ToObject<double>(this.serializer);

        public short ToInt16(object value) => ((JToken)value).ToObject<short>(this.serializer);

        public int ToInt32(object value) => ((JToken)value).ToObject<int>(this.serializer);

        public long ToInt64(object value) => ((JToken)value).ToObject<long>(this.serializer);

        public sbyte ToSByte(object value) => ((JToken)value).ToObject<sbyte>(this.serializer);

        public float ToSingle(object value) => ((JToken)value).ToObject<float>(this.serializer);

        public string? ToString(object value) => ((JToken)value).ToObject<string>(this.serializer);

        public ushort ToUInt16(object value) => ((JToken)value).ToObject<ushort>(this.serializer);

        public uint ToUInt32(object value) => ((JToken)value).ToObject<uint>(this.serializer);

        public ulong ToUInt64(object value) => ((JToken)value).ToObject<ulong>(this.serializer);
    }

    [RequiresUnreferencedCode(RuntimeReasons.LoadType)]
    private class ExceptionConverter : JsonConverter<Exception?>
    {
        /// <summary>
        /// Tracks recursion count while serializing or deserializing an exception.
        /// </summary>
        private static ThreadLocal<int> exceptionRecursionCounter = new();

        private readonly JsonMessageFormatter formatter;

        internal ExceptionConverter(JsonMessageFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override Exception? ReadJson(JsonReader reader, Type objectType, Exception? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            Assumes.NotNull(this.formatter.JsonRpc);
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            exceptionRecursionCounter.Value++;
            try
            {
                if (reader.TokenType != JsonToken.StartObject)
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

                SerializationInfo? info = new SerializationInfo(objectType, new JsonConverterFormatter(serializer));
                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.EndObject)
                    {
                        break;
                    }

                    if (reader.TokenType == JsonToken.PropertyName)
                    {
                        string name = (string)reader.Value!;
                        if (!reader.Read())
                        {
                            throw new EndOfStreamException();
                        }

                        JToken? value = reader.TokenType == JsonToken.Null ? null : JToken.Load(reader);
                        info.AddSafeValue(name, value);
                    }
                    else
                    {
                        throw new InvalidOperationException("Expected PropertyName token but encountered: " + reader.TokenType);
                    }
                }

                return ExceptionSerializationHelpers.Deserialize<Exception>(this.formatter.JsonRpc, info, this.formatter.JsonRpc.TrimUnsafeTypeLoader, this.formatter.JsonRpc?.TraceSource);
            }
            finally
            {
                exceptionRecursionCounter.Value--;
            }
        }

        public override void WriteJson(JsonWriter writer, Exception? value, JsonSerializer serializer)
        {
            if (value is null)
            {
                writer.WriteNull();
                return;
            }

            // We have to guard our own recursion because the serializer has no visibility into inner exceptions.
            // Each exception in the russian doll is a new serialization job from its perspective.
            exceptionRecursionCounter.Value++;
            try
            {
                if (exceptionRecursionCounter.Value > this.formatter.JsonRpc?.ExceptionOptions.RecursionLimit)
                {
                    // Exception recursion has gone too deep. Skip this value and write null as if there were no inner exception.
                    writer.WriteNull();
                    return;
                }

                SerializationInfo info = new SerializationInfo(value.GetType(), new JsonConverterFormatter(serializer));
                ExceptionSerializationHelpers.Serialize(value, info);
                writer.WriteStartObject();
                foreach (SerializationEntry element in info.GetSafeMembers())
                {
                    writer.WritePropertyName(element.Name);
                    serializer.Serialize(writer, element.Value);
                }

                writer.WriteEndObject();
            }
            finally
            {
                exceptionRecursionCounter.Value--;
            }
        }
    }

    private class MarshalContractResolver : IContractResolver
    {
        private readonly JsonMessageFormatter formatter;
        private readonly IContractResolver underlyingContractResolver;

        public MarshalContractResolver(JsonMessageFormatter formatter)
            : this(formatter, null)
        {
        }

        public MarshalContractResolver(JsonMessageFormatter formatter, IContractResolver? underlyingContractResolver)
        {
            this.formatter = formatter;
            this.underlyingContractResolver = underlyingContractResolver ?? new DefaultContractResolver();
        }

        public JsonContract ResolveContract(Type type)
        {
            if (this.formatter.TryGetMarshaledJsonConverter(type, out RpcMarshalableConverter? converter))
            {
                return new JsonObjectContract(type)
                {
                    Converter = converter,
                    CreatedType = type,
                };
            }

            JsonContract? result = this.underlyingContractResolver.ResolveContract(type);
            switch (result)
            {
                case JsonObjectContract objectContract:
                    foreach (JsonProperty property in objectContract.Properties)
                    {
                        if (property.Ignored)
                        {
                            continue;
                        }

                        if (property.PropertyType is not null && this.formatter.TryGetMarshaledJsonConverter(property.PropertyType, out RpcMarshalableConverter? propertyConverter))
                        {
                            property.Converter = propertyConverter;
                        }
                    }

                    break;
            }

            return result;
        }
    }
}
