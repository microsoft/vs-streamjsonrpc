// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using System.Buffers;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Optionally implemented by a <see cref="IJsonRpcMessageFormatter"/> when it needs the fully serialized sequence in order to trace the JSON representation of the message.
    /// </summary>
    public interface IJsonRpcFormatterTracingCallbacks
    {
        /// <summary>
        /// Invites the formatter to call <see cref="IJsonRpcTracingCallbacks.OnMessageSerialized(JsonRpcMessage, object)"/> with the JSON representation of the message just serialized..
        /// </summary>
        /// <param name="message">The message that was just serialized.</param>
        /// <param name="encodedMessage">The encoded copy of the message, as it recently came from the <see cref="IJsonRpcMessageFormatter.Serialize(IBufferWriter{byte}, JsonRpcMessage)"/> method.</param>
        void OnSerializationComplete(JsonRpcMessage message, ReadOnlySequence<byte> encodedMessage);
    }
}
