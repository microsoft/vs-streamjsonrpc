// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class JsonRpcError
    {
        /// <summary>
        /// The name of the error object's "data.stack" field within the data object.
        /// </summary>
        private const string DataStackFieldName = "stack";

        /// <summary>
        /// The name of the error object's "data.code" field within the data object.
        /// </summary>
        private const string DataCodeFieldName = "code";

        internal JsonRpcError(int code, string message)
            : this(code, message, data: null)
        {
        }

        [JsonConstructor]
        internal JsonRpcError(int code, string message, JObject data)
        {
            this.Code = code;
            this.Message = message;
            this.Data = data;
        }

        public string ErrorStack => this.Data?[DataStackFieldName]?.Type == JTokenType.String ? this.Data.Value<string>(DataStackFieldName) : null;

        public string ErrorCode => this.Data?[DataCodeFieldName]?.Type == JTokenType.String || this.Data?[DataCodeFieldName]?.Type == JTokenType.Integer ? this.Data.Value<string>(DataCodeFieldName) : null;

        [JsonProperty("code", Required = Required.Always)]
        internal int Code { get; private set; }

        [JsonProperty("message", Required = Required.Always)]
        internal string Message { get; private set; }

        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        internal JObject Data { get; private set; }
    }
}
