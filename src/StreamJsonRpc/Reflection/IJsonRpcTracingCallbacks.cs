// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An interface implemented by <see cref="JsonRpc"/> for <see cref="IJsonRpcMessageFormatter"/> implementations to use to facilitate message tracing.
    /// </summary>
    public interface IJsonRpcTracingCallbacks
    {
        /// <summary>
        /// Occurs when the <see cref="IJsonRpcMessageFormatter"/> has serialized a message for transmission.
        /// </summary>
        /// <param name="message">The JSON-RPC message.</param>
        /// <param name="encodedMessage">The encoded form of the message. Calling <see cref="object.ToString()"/> on this should produce the JSON-RPC text of the message.</param>
        void OnMessageSerialized(JsonRpcMessage message, object encodedMessage);

        /// <summary>
        /// Occurs when the <see cref="IJsonRpcMessageFormatter"/> has deserialized an incoming message.
        /// </summary>
        /// <param name="message">The JSON-RPC message.</param>
        /// <param name="encodedMessage">The encoded form of the message. Calling <see cref="object.ToString()"/> on this should produce the JSON-RPC text of the message.</param>
        void OnMessageDeserialized(JsonRpcMessage message, object encodedMessage);
    }
}
