// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using StreamJsonRpc.Protocol;
    using StreamJsonRpc.Reflection;

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
        /// <param name="bufferWriter">The receiver of the serialized bytes.</param>
        /// <param name="message">The message to serialize.</param>
        void Serialize(IBufferWriter<byte> bufferWriter, JsonRpcMessage message);

        /// <summary>
        /// Gets a JSON representation for a given message for tracing purposes.
        /// </summary>
        /// <param name="message">The message to be traced.</param>
        /// <returns>Any object whose <see cref="object.ToString()"/> method will produce a human-readable JSON string, suitable for tracing.</returns>
        [Obsolete("Tracing is now done via the " + nameof(IJsonRpcTracingCallbacks) + " and " + nameof(IJsonRpcFormatterTracingCallbacks) + " interfaces. Formatters may throw NotSupportedException from this method.")]
        object GetJsonText(JsonRpcMessage message);
    }
}
