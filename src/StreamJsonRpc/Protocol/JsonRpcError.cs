// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Protocol
{
    using System;
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
        [Obsolete("Use " + nameof(RequestId) + " instead.")]
        [IgnoreDataMember]
        public object Id
        {
            get => this.RequestId.ObjectValue;
            set => this.RequestId = RequestId.Parse(value);
        }

        /// <summary>
        /// Gets or sets an identifier established by the client if a response to the request is expected.
        /// </summary>
        [DataMember(Name = "id", Order = 2, IsRequired = true, EmitDefaultValue = true)]
        public RequestId RequestId { get; set; }

        /// <summary>
        /// Gets the string to display in the debugger for this instance.
        /// </summary>
        protected string DebuggerDisplay => $"Error: {this.Error.Code} \"{this.Error.Message}\" ({this.RequestId})";

        /// <inheritdoc/>
        public override string ToString()
        {
            return new JObject
            {
                new JProperty("id", this.RequestId.ObjectValue),
                new JProperty("error", new JObject
                {
                    new JProperty("code", this.Error?.Code),
                    new JProperty("message", this.Error?.Message),
                }),
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Describes the error.
        /// </summary>
        [DataContract]
        public class ErrorDetail
        {
            /// <summary>
            /// Gets or sets a number that indicates the error type that occurred.
            /// </summary>
            /// <value>
            /// The error codes from and including -32768 to -32000 are reserved for errors defined by the spec or this library.
            /// Codes outside that range are available for app-specific error codes.
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
        }
    }
}
