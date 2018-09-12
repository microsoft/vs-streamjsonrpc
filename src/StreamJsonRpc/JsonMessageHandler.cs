// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Uses Newtonsoft.Json serialization to translate <see cref="JToken"/> used by <see cref="IJsonMessageHandler"/>
    /// into <see cref="JsonRpcMessage"/> objects.
    /// </summary>
    internal class JsonMessageHandler : IMessageHandler, IDisposableObservable
    {
        /// <summary>
        /// The key into an <see cref="Exception.Data"/> dictionary whose value may be a <see cref="JToken"/> that failed deserialization.
        /// </summary>
        internal const string ExceptionDataKey = "JToken";

        /// <summary>
        /// The underlying <see cref="JToken"/>-based message handler.
        /// </summary>
        private readonly IJsonMessageHandler innerHandler;

        /// <summary>
        /// Backing field for the <see cref="IsDisposed"/> property.
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonMessageHandler"/> class.
        /// </summary>
        /// <param name="jsonMessageHandler">The <see cref="IJsonMessageHandler"/> to wrap.</param>
        public JsonMessageHandler(IJsonMessageHandler jsonMessageHandler)
        {
            this.innerHandler = jsonMessageHandler ?? throw new ArgumentNullException(nameof(jsonMessageHandler));
        }

        /// <inheritdoc/>
        public bool CanRead => this.innerHandler.CanRead;

        /// <inheritdoc/>
        public bool CanWrite => this.innerHandler.CanWrite;

        /// <inheritdoc/>
        public bool IsDisposed => (this.innerHandler as IDisposableObservable)?.IsDisposed ?? this.isDisposed;

        /// <inheritdoc/>
        public async ValueTask<JsonRpcMessage> ReadAsync(CancellationToken cancellationToken)
        {
            JToken json = await this.innerHandler.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (json == null)
            {
                return null;
            }

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

#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix
        /// <inheritdoc/>
        public ValueTask WriteAsync(JsonRpcMessage jsonRpcMessage, CancellationToken cancellationToken)
#pragma warning restore AvoidAsyncSuffix // Avoid Async suffix
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Pre-tokenize the user data so we can use their custom converters for just their data and not for the base message.
            this.TokenizeUserData(jsonRpcMessage);

            var json = JToken.FromObject(jsonRpcMessage);

            // Fix up dropped fields that are mandatory
            if (jsonRpcMessage is Protocol.JsonRpcResult && json["result"] == null)
            {
                json["result"] = JValue.CreateNull();
            }

            return this.innerHandler.WriteAsync(json, cancellationToken);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.isDisposed = true;
            (this.innerHandler as IDisposable)?.Dispose();
        }

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

        /// <summary>
        /// Converts user data to <see cref="JToken"/> objects using all applicable user-provided <see cref="JsonConverter"/> instances.
        /// </summary>
        /// <param name="jsonRpcMessage">A JSON-RPC message.</param>
        private void TokenizeUserData(JsonRpcMessage jsonRpcMessage)
        {
            if (jsonRpcMessage is Protocol.JsonRpcRequest request)
            {
                if (request.NamedArguments != null)
                {
                    request.NamedArguments = request.NamedArguments.ToDictionary(
                        kv => kv.Key,
                        kv => (object)this.TokenizeUserData(kv.Value));
                }
                else if (request.ArgumentsArray != null)
                {
                    request.ArgumentsArray = request.ArgumentsArray.Select(this.TokenizeUserData).ToArray();
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

            return JToken.FromObject(value, this.innerHandler.JsonSerializer);
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

            return new JsonRpcRequest(this.innerHandler.JsonSerializer)
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

            return new JsonRpcResult(this.innerHandler.JsonSerializer)
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
