// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
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
        /// UTF-8 encoding without a preamble.
        /// </summary>
        private static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// Backing field for the <see cref="Encoding"/> property.
        /// </summary>
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
            try
            {
                return
                    json["method"] != null ? this.ReadRequest(json) :
                    json["result"] != null ? this.ReadResult(json) :
                    json["error"] != null ? (JsonRpcMessage)this.ReadError(json) :
                    throw new JsonSerializationException("Unrecognized JSON-RPC message: " + json);
            }
            catch (JsonException exception)
            {
                var serializationException = new JsonSerializationException($"Unable to deserialize {nameof(JsonRpcMessage)}.", exception);
                serializationException.Data[ExceptionDataKey] = json;
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

        private void WriteJToken(IBufferWriter<byte> contentBuffer, JToken json)
        {
            using (var streamWriter = new StreamWriter(contentBuffer.AsStream(), this.Encoding, 4096))
            {
                using (var jsonWriter = new JsonTextWriter(streamWriter))
                {
                    json.WriteTo(jsonWriter);
                    jsonWriter.Flush();
                }
            }
        }

        private JToken ReadJToken(ReadOnlySequence<byte> contentBuffer, Encoding encoding)
        {
            Requires.NotNull(encoding, nameof(encoding));

            var jsonReader = new JsonTextReader(new StreamReader(contentBuffer.AsStream(), encoding));
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
                if (request.ArgumentsArray != null)
                {
                    request.ArgumentsArray = request.ArgumentsArray.Select(this.TokenizeUserData).ToArray();
                }
                else if (request.Arguments != null)
                {
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
                    Verify.Operation(!typeof(T).GetTypeInfo().IsValueType, "null result is not assignable to a value type.");
                    return default;
                }

                return result.ToObject<T>(this.jsonSerializer);
            }
        }
    }
}
