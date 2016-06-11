using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StreamJsonRpc
{
    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class JsonRpcMessage
    {
        private JsonRpcMessage()
        {
            this.JsonRpcVersion = "2.0";
        }

        private JsonRpcMessage(string method, object[] parameters, string id = null, string jsonrpc = "2.0")
        {
            this.Parameters = parameters?.Length == 0 ? null : JToken.FromObject(parameters);
            this.Method = method;
            this.Id = id;
            this.JsonRpcVersion = jsonrpc;
        }

        [JsonProperty("jsonrpc")]
        public string JsonRpcVersion { get; private set; }

        [JsonProperty("method")]
        public string Method { get; private set; }

        [JsonProperty("id")]
        public string Id { get; private set; }

        [JsonProperty("error")]
        public JsonRpcError Error { get; private set; }

        [JsonProperty("params")]
        private JToken Parameters
        {
            get;
            set;
        }

        [JsonProperty("result")]
        private JToken Result
        {
            get;
            set;
        }

        public bool IsRequest => !string.IsNullOrEmpty(this.Method);

        public bool IsResponse => this.IsError || this.Result != null;

        public bool IsError => this.Error != null;

        public bool IsNotification => this.Id == null;

        public int ParameterCount => this.Parameters != null ? this.Parameters.Children().Count() : 0;

        public static JsonRpcMessage CreateRequest(string id, string @method, object[] parameters)
        {
            return new JsonRpcMessage(method, parameters, id);
        }

        public static JsonRpcMessage CreateResult(string id, object result)
        {
            return new JsonRpcMessage()
            {
                Id = id,
                Result = result != null ? JToken.FromObject(result) : JValue.CreateNull(),
            };
        }

        public static JsonRpcMessage CreateError(string id, JsonRpcErrorCode error, string message)
        {
            return CreateError(id, (int)error, message);
        }

        public static JsonRpcMessage CreateError(string id, JsonRpcErrorCode error, string message, object data)
        {
            return CreateError(id, (int)error, message, data);
        }

        public static JsonRpcMessage CreateError(string id, int error, string message)
        {
            return CreateError(id, error, message, data: null);
        }

        public static JsonRpcMessage CreateError(string id, int error, string message, object data)
        {
            return new JsonRpcMessage()
            {
                Id = id,
                Error = new JsonRpcError(error, message, data),
            };
        }

        public T GetResult<T>()
        {
            return this.Result == null ? default(T) : this.Result.ToObject<T>();
        }

        public object[] GetParameters(ParameterInfo[] parameterInfos)
        {
            if (this.Parameters == null || !this.Parameters.Children().Any())
            {
                return new object[0];
            }

            if (parameterInfos == null || parameterInfos.Length == 0)
            {
                return this.Parameters.ToObject<object[]>();
            }

            int index = 0;
            var result = new List<object>(parameterInfos.Length);
            foreach (var parameter in this.Parameters.Children())
            {
                Type type = typeof(object);
                if (index < parameterInfos.Length)
                {
                    type = parameterInfos[index].ParameterType;
                    index++;
                }

                object value = parameter.ToObject(type);
                result.Add(value);
            }

            for (;index < parameterInfos.Length; index++)
            {
                result.Add(parameterInfos[index].HasDefaultValue ? parameterInfos[index].DefaultValue : null);
            }

            return result.ToArray();
        }

        public string ToJson()
        {
            var settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
            };

            return JsonConvert.SerializeObject(this, settings);
        }

        public static JsonRpcMessage FromJson(string json)
        {
            var settings = new JsonSerializerSettings
            {
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
            };

            return JsonConvert.DeserializeObject<JsonRpcMessage>(json, settings);
        }

        public static JsonRpcMessage FromJson(JsonReader reader)
        {
            var settings = new JsonSerializerSettings
            {
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
            };

            JsonSerializer serializer = JsonSerializer.Create(settings);

            JsonRpcMessage result = serializer.Deserialize<JsonRpcMessage>(reader);
            if (result == null)
            {
                throw new JsonException(Resources.JsonRpcCannotBeNull);
            }

            return result;
        }
    }
}