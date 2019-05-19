// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Runtime.Serialization;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Nerdbank.Streams;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Uses Newtonsoft.Json serialization to serialize <see cref="JsonRpcMessage"/> as JSON (text).
    /// </summary>
    public class JsonMessageFormatter : IJsonRpcMessageTextFormatter
    {
        /// <summary>
        /// The key into an <see cref="Exception.Data"/> dictionary whose value may be a <see cref="JToken"/> that failed deserialization.
        /// </summary>
        internal const string ExceptionDataKey = "JToken";

        /// <summary>
        /// A collection of supported protocol versions.
        /// </summary>
        private static readonly IReadOnlyCollection<Version> SupportedProtocolVersions = new Version[] { new Version(1, 0), new Version(2, 0) };

        /// <summary>
        /// UTF-8 encoding without a preamble.
        /// </summary>
        private static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// The reusable <see cref="TextWriter"/> to use with newtonsoft.json's serializer.
        /// </summary>
        private readonly BufferTextWriter bufferTextWriter = new BufferTextWriter();

        /// <summary>
        /// The reusable <see cref="TextReader"/> to use with newtonsoft.json's deserializer.
        /// </summary>
        private readonly SequenceTextReader sequenceTextReader = new SequenceTextReader();

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
        /// Initializes a new instance of the <see cref="JsonMessageFormatter"/> class
        /// that uses <see cref="Encoding.UTF8"/> (without the preamble) for its text encoding.
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
            this.Encoding = encoding;
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
        public JsonSerializer JsonSerializer { get; } = new JsonSerializer()
        {
            NullValueHandling = NullValueHandling.Ignore,
            ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
        };

        /// <inheritdoc/>
        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer) => this.Deserialize(contentBuffer, this.Encoding);

        /// <inheritdoc/>
        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer, Encoding encoding)
        {
            Requires.NotNull(encoding, nameof(encoding));

            JToken json = this.ReadJToken(contentBuffer, encoding);
            return this.Deserialize(json);
        }

        /// <inheritdoc/>
        public void Serialize(IBufferWriter<byte> contentBuffer, JsonRpcMessage message)
        {
            JToken json = this.Serialize(message);

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

            try
            {
                switch (this.ProtocolVersion.Major)
                {
                    case 1:
                        this.VerifyProtocolCompliance(json["jsonrpc"] == null, json, "\"jsonrpc\" property not expected. Use protocol version 2.0.");
                        this.VerifyProtocolCompliance(json["id"] != null, json, "\"id\" property missing.");
                        return
                            json["method"] != null ? this.ReadRequest(json) :
                            json["error"]?.Type == JTokenType.Null ? this.ReadResult(json) :
                            json["error"]?.Type != JTokenType.Null ? (JsonRpcMessage)this.ReadError(json) :
                            throw this.CreateProtocolNonComplianceException(json);
                    case 2:
                        this.VerifyProtocolCompliance(json.Value<string>("jsonrpc") == "2.0", json, $"\"jsonrpc\" property must be set to \"2.0\", or set {nameof(this.ProtocolVersion)} to 1.0 mode.");
                        return
                            json["method"] != null ? this.ReadRequest(json) :
                            json["result"] != null ? this.ReadResult(json) :
                            json["error"] != null ? (JsonRpcMessage)this.ReadError(json) :
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
            // Pre-tokenize the user data so we can use their custom converters for just their data and not for the base message.
            this.TokenizeUserData(message);

            var json = JToken.FromObject(message);

            // Fix up dropped fields that are mandatory
            if (message is Protocol.JsonRpcResult && json["result"] == null)
            {
                json["result"] = JValue.CreateNull();
            }

            if (this.ProtocolVersion.Major == 1 && json["id"] == null)
            {
                // JSON-RPC 1.0 requires the id property to be present even for notifications.
                json["id"] = JValue.CreateNull();
            }

            return json;
        }

        /// <inheritdoc/>
        public object GetJsonText(JsonRpcMessage message) => JToken.FromObject(message);

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

        private static object GetNormalizedId(JToken idToken)
        {
            return
                idToken?.Type == JTokenType.Integer ? idToken.Value<long>() :
                idToken?.Type == JTokenType.String ? idToken.Value<string>() :
                idToken == null ? (object)null :
                throw new JsonSerializationException("Unexpected type for id property: " + idToken.Type);
        }

        private void VerifyProtocolCompliance(bool condition, JToken message, string explanation = null)
        {
            if (!condition)
            {
                throw this.CreateProtocolNonComplianceException(message, explanation);
            }
        }

        private Exception CreateProtocolNonComplianceException(JToken message, string explanation = null)
        {
            var builder = new StringBuilder();
            builder.AppendFormat(CultureInfo.CurrentCulture, "Unrecognized JSON-RPC {0} message", this.ProtocolVersion);
            if (explanation != null)
            {
                builder.AppendFormat(" ({0})", explanation);
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
                json.WriteTo(jsonWriter);
                jsonWriter.Flush();
            }
        }

        private JToken ReadJToken(ReadOnlySequence<byte> contentBuffer, Encoding encoding)
        {
            Requires.NotNull(encoding, nameof(encoding));

            this.sequenceTextReader.Initialize(contentBuffer, encoding);
            var jsonReader = new JsonTextReader(this.sequenceTextReader)
            {
                CloseInput = true,
                Culture = this.JsonSerializer.Culture,
                DateFormatString = this.JsonSerializer.DateFormatString,
                DateParseHandling = this.JsonSerializer.DateParseHandling,
                DateTimeZoneHandling = this.JsonSerializer.DateTimeZoneHandling,
                FloatParseHandling = this.JsonSerializer.FloatParseHandling,
                MaxDepth = this.JsonSerializer.MaxDepth,
            };
            JToken json = JToken.ReadFrom(jsonReader);
            return json;
        }

        /// <summary>
        /// Converts user data to <see cref="JToken"/> objects using all applicable user-provided <see cref="JsonConverter"/> instances.
        /// </summary>
        /// <param name="jsonRpcMessage">A JSON-RPC message.</param>
        private void TokenizeUserData(JsonRpcMessage jsonRpcMessage)
        {
            if (jsonRpcMessage is Protocol.JsonRpcRequest request)
            {
                if (request.ArgumentsList != null)
                {
                    request.ArgumentsList = request.ArgumentsList.Select(this.TokenizeUserData).ToArray();
                }
                else if (request.Arguments != null)
                {
                    if (this.ProtocolVersion.Major < 2)
                    {
                        throw new NotSupportedException(Resources.ParameterObjectsNotSupportedInJsonRpc10);
                    }

                    // Tokenize the user data using the user-supplied serializer.
                    var paramsObject = JObject.FromObject(request.Arguments, this.JsonSerializer);
                    request.Arguments = paramsObject;
                }
            }
            else if (jsonRpcMessage is Protocol.JsonRpcResult result)
            {
                result.Result = this.TokenizeUserData(result.Result);
            }
        }

        /// <summary>
        /// Converts a single user data value to a <see cref="JToken"/>, using all applicable user-provided <see cref="JsonConverter"/> instances.
        /// </summary>
        /// <param name="value">The value to tokenize.</param>
        /// <returns>The <see cref="JToken"/> instance.</returns>
        private JToken TokenizeUserData(object value)
        {
            if (value is JToken token)
            {
                return token;
            }

            if (value == null)
            {
                return JValue.CreateNull();
            }

            return JToken.FromObject(value, this.JsonSerializer);
        }

        private JsonRpcRequest ReadRequest(JToken json)
        {
            Requires.NotNull(json, nameof(json));

            JToken id = json["id"];

            // We leave arguments as JTokens at this point, so that we can try deserializing them
            // to more precise .NET types as required by the method we're invoking.
            JToken args = json["params"];
            object arguments =
                args is JObject argsObject ? PartiallyParseNamedArguments(argsObject) :
                args is JArray argsArray ? (object)PartiallyParsePositionalArguments(argsArray) :
                null;

            return new JsonRpcRequest(this.JsonSerializer)
            {
                Id = GetNormalizedId(id),
                Method = json.Value<string>("method"),
                Arguments = arguments,
            };
        }

        private JsonRpcResult ReadResult(JToken json)
        {
            Requires.NotNull(json, nameof(json));

            JToken id = json["id"];
            JToken result = json["result"];

            return new JsonRpcResult(this.JsonSerializer)
            {
                Id = GetNormalizedId(id),
                Result = result,
            };
        }

        private JsonRpcError ReadError(JToken json)
        {
            Requires.NotNull(json, nameof(json));

            JToken id = json["id"];
            JToken error = json["error"];

            return new JsonRpcError
            {
                Id = GetNormalizedId(id),
                Error = new JsonRpcError.ErrorDetail
                {
                    Code = (JsonRpcErrorCode)error.Value<long>("code"),
                    Message = error.Value<string>("message"),
                    Data = error["data"], // leave this as a JToken
                },
            };
        }

        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        [DataContract]
        private class JsonRpcRequest : Protocol.JsonRpcRequest
        {
            private readonly JsonSerializer jsonSerializer;

            internal JsonRpcRequest(JsonSerializer jsonSerializer)
            {
                this.jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            }

            public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, Span<object> typedArguments)
            {
                // Special support for accepting a single JToken instead of all parameters individually.
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(JToken) && this.NamedArguments != null)
                {
                    var obj = new JObject();
                    foreach (var property in this.NamedArguments)
                    {
                        obj.Add(new JProperty(property.Key, property.Value));
                    }

                    typedArguments[0] = obj;
                    return ArgumentMatchResult.Success;
                }

                return base.TryGetTypedArguments(parameters, typedArguments);
            }

            public override bool TryGetArgumentByNameOrIndex(string name, int position, Type typeHint, out object value)
            {
                if (base.TryGetArgumentByNameOrIndex(name, position, typeHint, out value))
                {
                    var token = (JToken)value;
                    try
                    {
                        value = token.ToObject(typeHint, this.jsonSerializer);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }

                return false;
            }
        }

        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        [DataContract]
        private class JsonRpcResult : Protocol.JsonRpcResult
        {
            private readonly JsonSerializer jsonSerializer;

            internal JsonRpcResult(JsonSerializer jsonSerializer)
            {
                this.jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            }

            public override T GetResult<T>()
            {
                var result = (JToken)this.Result;
                if (result.Type == JTokenType.Null)
                {
                    Verify.Operation(!typeof(T).GetTypeInfo().IsValueType || Nullable.GetUnderlyingType(typeof(T)) != null, "null result is not assignable to a value type.");
                    return default;
                }

                return result.ToObject<T>(this.jsonSerializer);
            }
        }
    }
}
