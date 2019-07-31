// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System.Buffers;
    using System.IO.Pipelines;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An interface that offers <see cref="JsonRpcMessage"/> serialization to an <see cref="IBufferWriter{T}"/> and asynchronous deserialization
    /// and formats messages as JSON (text).
    /// </summary>
    public interface IJsonRpcAsyncMessageTextFormatter : IJsonRpcAsyncMessageFormatter, IJsonRpcMessageTextFormatter
    {
        /// <summary>
        /// Deserializes a sequence of bytes to a <see cref="JsonRpcMessage"/>.
        /// </summary>
        /// <param name="reader">The reader to deserialize from.</param>
        /// <param name="encoding">The encoding to read the bytes from <paramref name="reader"/> with. Must not be null.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>The deserialized message.</returns>
        ValueTask<JsonRpcMessage> DeserializeAsync(PipeReader reader, Encoding encoding, CancellationToken cancellationToken);
    }
}
