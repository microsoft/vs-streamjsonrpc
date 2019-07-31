// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System.Buffers;
    using System.IO.Pipelines;
    using System.Threading;
    using System.Threading.Tasks;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An interface that offers <see cref="JsonRpcMessage"/> serialization to an <see cref="IBufferWriter{T}"/> and asynchronous deserialization.
    /// </summary>
    public interface IJsonRpcAsyncMessageFormatter : IJsonRpcMessageFormatter
    {
        /// <summary>
        /// Deserializes a <see cref="JsonRpcMessage"/>.
        /// </summary>
        /// <param name="reader">The reader to deserialize from.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>The deserialized <see cref="JsonRpcMessage"/>.</returns>
        ValueTask<JsonRpcMessage> DeserializeAsync(PipeReader reader, CancellationToken cancellationToken);
    }
}
