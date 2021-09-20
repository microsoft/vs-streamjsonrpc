// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Protocol
{
    /// <summary>
    /// An interface found on JSON-RPC protocol messages that contain an <pre>id</pre> field.
    /// </summary>
    public interface IJsonRpcMessageWithId
    {
        /// <summary>
        /// Gets or sets the ID on a message.
        /// </summary>
        RequestId RequestId { get; set; }
    }
}
