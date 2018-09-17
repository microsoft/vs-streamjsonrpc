// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System.Buffers;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An interface that offers <see cref="JsonRpcMessage"/> serialization to and from a sequence of bytes.
    /// </summary>
    public interface IJsonRpcMessageFormatter
    {
        /// <summary>
        /// Deserializes a <see cref="JsonRpcMessage"/>.
        /// </summary>
        /// <param name="contentBuffer">A sequence of bytes to deserialize.</param>
        /// <returns>The deserialized <see cref="JsonRpcMessage"/>.</returns>
        JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer);

        /// <summary>
        /// Serializes a <see cref="JsonRpcMessage"/>.
        /// </summary>
        /// <param name="contentBuffer">The receiver of the serialized bytes.</param>
        /// <param name="message">The message to serialize.</param>
        void Serialize(IBufferWriter<byte> contentBuffer, JsonRpcMessage message);
    }
}
