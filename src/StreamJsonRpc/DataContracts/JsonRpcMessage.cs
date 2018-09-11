// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.Serialization;
    using System.Threading;
    using Microsoft;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    [DataContract]
    internal sealed class JsonRpcMessage
    {
        private JsonRpcMessage()
        {
            this.JsonRpcVersion = "2.0";
        }

        private JsonRpcMessage(string method, JToken parameters, object id = null, string jsonrpc = "2.0")
        {
            this.Parameters = (JContainer)parameters; // must be an array, an object, or null.
            this.Method = method;
            this.Id = id != null ? JToken.FromObject(id) : null;
            this.JsonRpcVersion = jsonrpc;
        }

        [DataMember(Name = "jsonrpc", Order = 1)]
        public string JsonRpcVersion { get; private set; }

        [DataMember(Name = "method", Order = 2)]
        public string Method { get; private set; }

        [DataMember(Name = "id", Order = 3)]
        public JToken Id { get; private set; }

        [DataMember(Name = "error", Order = 4)]
        public JsonRpcError Error { get; private set; }

        [DataMember(Name = "params", Order = 5)]
        public JContainer Parameters
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the <see cref="JObject"/> that was originally deserialized into this message, if applicable.
        /// </summary>
        public JObject OriginalRepresentation { get; set; }

        internal bool IsRequest => !string.IsNullOrEmpty(this.Method);

        internal bool IsResponse => this.IsError || this.Result != null;

        internal bool IsError => this.Error != null;

        internal bool IsNotification => this.Id == null;

        internal int ParameterCount => this.Parameters?.Count ?? 0;

        [DataMember(Name = "result", Order = 6)]
        private JToken Result
        {
            get;
            set;
        }

        internal static JsonRpcMessage CreateRequest(int? id, string @method, IReadOnlyList<object> parameters, JsonSerializer parametersSerializer)
        {
            return new JsonRpcMessage(method, JToken.FromObject(parameters, parametersSerializer), id);
        }

        internal static JsonRpcMessage CreateRequestWithNamedParameters(int? id, string @method, object namedParameters, JsonSerializer parameterSerializer)
        {
            if (namedParameters == null)
            {
                return new JsonRpcMessage(method, null, id);
            }
            else
            {
                return new JsonRpcMessage(method, JToken.FromObject(namedParameters, parameterSerializer), id);
            }
        }

        internal static JsonRpcMessage CreateResult(JToken id, object result, JsonSerializer jsonSerializer)
        {
            return new JsonRpcMessage()
            {
                Id = id,
                Result = result != null ? JToken.FromObject(result, jsonSerializer) : JValue.CreateNull(),
            };
        }

        internal static JsonRpcMessage CreateError(JToken id, JsonRpcErrorCode error, string message)
        {
            return CreateError(id, (int)error, message);
        }

        internal static JsonRpcMessage CreateError(JToken id, JsonRpcErrorCode error, string message, JObject data)
        {
            return CreateError(id, (int)error, message, data);
        }

        internal static JsonRpcMessage CreateError(JToken id, int error, string message)
        {
            return CreateError(id, error, message, data: null);
        }

        internal static JsonRpcMessage CreateError(JToken id, int error, string message, JObject data)
        {
            return new JsonRpcMessage()
            {
                Id = id,
                Error = new JsonRpcError(error, message, data),
            };
        }

        internal static JsonRpcMessage FromJson(JToken json, JsonSerializerSettings settings)
        {
            Requires.NotNull(json, nameof(json));
            Requires.NotNull(settings, nameof(settings));

            JsonSerializer serializer = JsonSerializer.Create(settings);
            var message = json.ToObject<JsonRpcMessage>(serializer);
            message.OriginalRepresentation = (JObject)json;
            return message;
        }

        internal T GetResult<T>(JsonSerializer serializer)
        {
            Requires.NotNull(serializer, nameof(serializer));
            return this.Result == null ? default(T) : this.Result.ToObject<T>(serializer);
        }

        internal object[] GetParameters(ParameterInfo[] parameterInfos, JsonSerializer jsonSerializer)
        {
            Requires.NotNull(parameterInfos, nameof(parameterInfos));
            Requires.NotNull(jsonSerializer, nameof(jsonSerializer));

            int index = 0;
            var result = new object[parameterInfos.Length];
            foreach (var parameter in this.Parameters?.Children() ?? Enumerable.Empty<JToken>())
            {
                object value = parameter.ToObject(parameterInfos[index].ParameterType, jsonSerializer);
                result[index++] = value;
            }

            for (; index < parameterInfos.Length; index++)
            {
                object value =
                    parameterInfos[index].HasDefaultValue ? parameterInfos[index].DefaultValue :
                    parameterInfos[index].ParameterType == typeof(CancellationToken) ? (CancellationToken?)CancellationToken.None :
                    null;
                result[index++] = value;
            }

            return result;
        }
    }
}