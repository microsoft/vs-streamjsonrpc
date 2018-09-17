// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Protocol
{
    using System.Runtime.Serialization;

    /// <summary>
    /// The base class for a JSON-RPC request or response.
    /// </summary>
    [DataContract]
    [KnownType(typeof(JsonRpcRequest))]
    [KnownType(typeof(JsonRpcResult))]
    [KnownType(typeof(JsonRpcError))]
    public abstract class JsonRpcMessage
    {
        /// <summary>
        /// Gets or sets the version of the JSON-RPC protocol that this message conforms to.
        /// </summary>
        /// <value>Defaults to "2.0".</value>
        [DataMember(Name = "jsonrpc", Order = 0, IsRequired = true)]
        public string Version { get; set; } = "2.0";
    }
}
