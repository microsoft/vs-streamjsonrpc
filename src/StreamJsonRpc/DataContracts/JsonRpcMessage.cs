// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using Microsoft;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class JsonRpcMessage
    {
        private JsonRpcMessage()
        {
            this.JsonRpcVersion = "2.0";
        }

        private JsonRpcMessage(string method, JToken parameters, object id = null, string jsonrpc = "2.0")
        {
            this.Parameters = parameters;
            this.Method = method;
            this.Id = id != null ? JToken.FromObject(id) : null;
            this.JsonRpcVersion = jsonrpc;
        }

        [JsonProperty("jsonrpc")]
        public string JsonRpcVersion { get; private set; }

        [JsonProperty("method")]
        public string Method { get; private set; }

        [JsonProperty("id")]
        public JToken Id { get; private set; }

        [JsonProperty("error")]
        public JsonRpcError Error { get; private set; }

        [JsonProperty("params")]
        public JToken Parameters
        {
            get;
            set;
        }

        public bool IsRequest => !string.IsNullOrEmpty(this.Method);

        public bool IsResponse => this.IsError || this.Result != null;

        public bool IsError => this.Error != null;

        public bool IsNotification => this.Id == null;

        public int ParameterCount => this.Parameters != null ? this.Parameters.Children().Count() : 0;

        [JsonProperty("result")]
        private JToken Result
        {
            get;
            set;
        }

        public static JsonRpcMessage CreateRequest(int? id, string @method, IReadOnlyList<object> parameters, JsonSerializer parametersSerializer)
        {
            return new JsonRpcMessage(method, JToken.FromObject(parameters, parametersSerializer), id);
        }

        public static JsonRpcMessage CreateRequestWithNamedParameters(int? id, string @method, object namedParameters, JsonSerializer parameterSerializer)
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

        public static JsonRpcMessage CreateResult(JToken id, object result, JsonSerializer jsonSerializer)
        {
            return new JsonRpcMessage()
            {
                Id = id,
                Result = result != null ? JToken.FromObject(result, jsonSerializer) : JValue.CreateNull(),
            };
        }

        public static JsonRpcMessage CreateError(JToken id, JsonRpcErrorCode error, string message)
        {
            return CreateError(id, (int)error, message);
        }

        public static JsonRpcMessage CreateError(JToken id, JsonRpcErrorCode error, string message, JObject data)
        {
            return CreateError(id, (int)error, message, data);
        }

        public static JsonRpcMessage CreateError(JToken id, int error, string message)
        {
            return CreateError(id, error, message, data: null);
        }

        public static JsonRpcMessage CreateError(JToken id, int error, string message, JObject data)
        {
            return new JsonRpcMessage()
            {
                Id = id,
                Error = new JsonRpcError(error, message, data),
            };
        }

        public static JsonRpcMessage FromJson(string json, JsonSerializerSettings settings)
        {
            return JsonConvert.DeserializeObject<JsonRpcMessage>(json, settings);
        }

        public static JsonRpcMessage FromJson(JsonReader reader, JsonSerializerSettings settings)
        {
            JsonSerializer serializer = JsonSerializer.Create(settings);

            JsonRpcMessage result = serializer.Deserialize<JsonRpcMessage>(reader);
            if (result == null)
            {
                throw new JsonException(Resources.JsonRpcCannotBeNull);
            }

            return result;
        }

        public T GetResult<T>(JsonSerializer serializer)
        {
            return this.Result == null ? default(T) : this.Result.ToObject<T>(serializer);
        }

        public object[] GetParameters(ParameterInfo[] parameterInfos, JsonSerializer jsonSerializer)
        {
            Requires.NotNull(parameterInfos, nameof(parameterInfos));
            Requires.NotNull(jsonSerializer, nameof(jsonSerializer));

            int index = 0;
            var result = new List<object>(parameterInfos.Length);
            foreach (var parameter in this.Parameters?.Children() ?? Enumerable.Empty<JToken>())
            {
                Type type = typeof(object);
                if (index < parameterInfos.Length)
                {
                    type = parameterInfos[index].ParameterType;
                    index++;
                }

                object value = parameter.ToObject(type, jsonSerializer);
                result.Add(value);
            }

            for (; index < parameterInfos.Length; index++)
            {
                object value =
                    parameterInfos[index].HasDefaultValue ? parameterInfos[index].DefaultValue :
                    parameterInfos[index].ParameterType == typeof(CancellationToken) ? (CancellationToken?)CancellationToken.None :
                    null;
                result.Add(value);
            }

            return result.ToArray();
        }

        public string ToJson(Formatting formatting, JsonSerializerSettings settings)
        {
            return JsonConvert.SerializeObject(this, formatting, settings);
        }
    }
}