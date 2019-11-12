// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An interface that may be found on an <see cref="IJsonRpcMessageHandler"/> object to request notification of when
    /// message deserialization is completed so buffers can be released or safely recycled.
    /// </summary>
    public interface IJsonRpcMessageBufferManager
    {
        /// <summary>
        /// Notifies that it is safe to free buffers held to deserialize the payload for a message because all deserialization attempts are completed.
        /// </summary>
        /// <param name="message">The message whose deserialization is done.</param>
        /// <remarks>
        /// Implementations are guaranteed to be called at least once for each message when deserialization is completed.
        /// This method will be invoked before the next request to <see cref="IJsonRpcMessageHandler.ReadAsync(System.Threading.CancellationToken)"/>.
        /// </remarks>
        void DeserializationComplete(JsonRpcMessage message);
    }
}
