// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Protocol
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.Serialization;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Describes the error resulting from a <see cref="JsonRpcRequest"/> that failed on the server.
    /// </summary>
    [DataContract]
    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + "}")]
    public class JsonRpcError : JsonRpcMessage, IJsonRpcMessageWithId
    {
        /// <summary>
        /// Gets or sets the detail about the error.
        /// </summary>
        [DataMember(Name = "error", Order = 1, IsRequired = true)]
        public ErrorDetail Error { get; set; }

        /// <summary>
        /// Gets or sets an identifier established by the client if a response to the request is expected.
        /// </summary>
        /// <value>A <see cref="string"/>, an <see cref="int"/>, a <see cref="long"/>, or <c>null</c>.</value>
        [DataMember(Name = "id", Order = 2, IsRequired = true, EmitDefaultValue = true)]
        public object Id { get; set; }

        /// <summary>
        /// Gets the string to display in the debugger for this instance.
        /// </summary>
        protected string DebuggerDisplay => $"Error: {this.Error.Code} \"{this.Error.Message}\" ({this.Id})";

        /// <summary>
        /// Describes the error.
        /// </summary>
        [DataContract]
        public class ErrorDetail
        {
            /// <summary>
            /// The name of the error object's "data.stack" field within the data object.
            /// </summary>
            private const string DataStackFieldName = "stack";

            /// <summary>
            /// The name of the error object's "data.code" field within the data object.
            /// </summary>
            private const string DataCodeFieldName = "code";

            /// <summary>
            /// Gets or sets a number that indicates the error type that occurred.
            /// </summary>
            /// <value>
            /// The error codes from and including -32768 to -32000 are reserved for pre-defined errors.
            /// </value>
            [DataMember(Name = "code", Order = 0, IsRequired = true)]
            public JsonRpcErrorCode Code { get; set; }

            /// <summary>
            /// Gets or sets a short description of the error.
            /// </summary>
            /// <remarks>
            /// The message SHOULD be limited to a concise single sentence.
            /// </remarks>
            [DataMember(Name = "message", Order = 1, IsRequired = true)]
            public string Message { get; set; }

            /// <summary>
            /// Gets or sets additional data about the error.
            /// </summary>
            [DataMember(Name = "data", Order = 2, IsRequired = false)]
            public object Data { get; set; }

            /// <summary>
            /// Gets the callstack of an exception thrown by the server.
            /// </summary>
            [IgnoreDataMember]
            public virtual string ErrorStack
            {
                get
                {
                    return
                        this.Data is JObject dataObject && dataObject[DataStackFieldName]?.Type == JTokenType.String ? dataObject.Value<string>(DataStackFieldName) :
                        this.Data is Dictionary<object, object> data && data.TryGetValue(DataStackFieldName, out var stack) && stack is string stackString ? stackString :
                        null;
                }
            }

            /// <summary>
            /// Gets the server error code.
            /// </summary>
            [IgnoreDataMember]
            public virtual string ErrorCode
            {
                get
                {
                    return
                        this.Data is JObject dataObject && (dataObject[DataCodeFieldName]?.Type == JTokenType.String || dataObject?[DataCodeFieldName]?.Type == JTokenType.Integer) ? dataObject.Value<string>(DataCodeFieldName) :
                        this.Data is Dictionary<object, object> data && data.TryGetValue(DataCodeFieldName, out var code) && (code is string || code is int) ? code.ToString() :
                        null;
                }
            }
        }
    }
}
