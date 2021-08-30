// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An interface that allows <see cref="IJsonRpcMessageFormatter"/> instances to act as a factory for <see cref="JsonRpcMessage"/>-derived types.
    /// </summary>
    public interface IJsonRpcMessageFactory
    {
        /// <summary>
        /// Creates an instance of <see cref="JsonRpcRequest"/> suitable for transmission over the <see cref="IJsonRpcMessageFormatter"/>.
        /// </summary>
        /// <returns>An instance of <see cref="JsonRpcRequest"/>.</returns>
        JsonRpcRequest CreateRequestMessage();

        /// <summary>
        /// Creates an instance of <see cref="JsonRpcError"/> suitable for transmission over the <see cref="IJsonRpcMessageFormatter"/>.
        /// </summary>
        /// <returns>An instance of <see cref="JsonRpcError"/>.</returns>
        JsonRpcError CreateErrorMessage();

        /// <summary>
        /// Creates an instance of <see cref="JsonRpcResult"/> suitable for transmission over the <see cref="IJsonRpcMessageFormatter"/>.
        /// </summary>
        /// <returns>An instance of <see cref="JsonRpcResult"/>.</returns>
        JsonRpcResult CreateResultMessage();
    }
}
