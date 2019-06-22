// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;

    /// <summary>
    /// Implemented by dynamically generated proxies returned from <see cref="JsonRpc.Attach{T}(IJsonRpcMessageHandler, JsonRpcProxyOptions)"/> and its overloads
    /// to provide access to additional JSON-RPC functionality.
    /// </summary>
    public interface IJsonRpcClientProxy : IDisposable
    {
        /// <summary>
        /// Gets the <see cref="StreamJsonRpc.JsonRpc"/> instance behind this proxy.
        /// </summary>
        JsonRpc JsonRpc { get; }
    }
}
