// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An interface that allows <see cref="IJsonRpcMessageFormatter"/> instances to offer custom types for <see cref="JsonRpcMessage"/> types.
    /// </summary>
    public interface IJsonRpcMessageFactory
    {
        /// <summary>
        /// Creates an instance of <see cref="JsonRpcRequest"/> that may contain additional support such as top level properties.
        /// </summary>
        /// <returns>an instance of <see cref="JsonRpcRequest"/>.</returns>
        JsonRpcRequest CreateRequestMessage();

        /// <summary>
        /// Creates an instance of <see cref="JsonRpcError"/> that may contain additional support such as top level properties.
        /// </summary>
        /// <returns>an instance of <see cref="JsonRpcError"/>.</returns>
        JsonRpcError CreateErrorMessage();

        /// <summary>
        /// Creates an instance of <see cref="JsonRpcResult"/> that may contain additional support such as top level properties.
        /// </summary>
        /// <returns>an instance of <see cref="JsonRpcResult"/>.</returns>
        JsonRpcResult CreateResultMessage();
    }
}
