// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System.Runtime.Serialization;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    [DataContract]
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
        internal JsonRpcError(int code, string message, JToken data)
        {
            this.Code = code;
            this.Message = message;
            this.Data = data;
        }

        public string ErrorStack => this.Data is JObject && this.Data?[DataStackFieldName]?.Type == JTokenType.String ? this.Data.Value<string>(DataStackFieldName) : null;

        public string ErrorCode => this.Data is JObject && (this.Data?[DataCodeFieldName]?.Type == JTokenType.String || this.Data?[DataCodeFieldName]?.Type == JTokenType.Integer) ? this.Data.Value<string>(DataCodeFieldName) : null;

        [DataMember(Name = "code", Order = 1, IsRequired = true)]
        internal int Code { get; private set; }

        [DataMember(Name = "message", Order = 2, IsRequired = true)]
        internal string Message { get; private set; }

        [DataMember(Name = "data", Order = 3, EmitDefaultValue = false)]
        internal JToken Data { get; private set; }
    }
}
