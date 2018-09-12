// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

#pragma warning disable AvoidAsyncSuffix // Avoid Async suffix

    /// <summary>
    /// The contract for sending and receiving JSON-RPC messages.
    /// </summary>
    public interface IJsonMessageHandler
    {
        /// <summary>
        /// Gets a value indicating whether this message handler can receive messages.
        /// </summary>
        bool CanRead { get; }

        /// <summary>
        /// Gets a value indicating whether this message handler can send messages.
        /// </summary>
        bool CanWrite { get; }

        /// <summary>
        /// Gets the <see cref="JsonSerializer"/> used when serializing and deserializing method arguments and return values.
        /// </summary>
        JsonSerializer JsonSerializer { get; }

        /// <summary>
        /// Reads a distinct and complete message from the transport, waiting for one if necessary.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the read request.</param>
        /// <returns>The JSON token, or <c>null</c> if the underlying transport ends before beginning another message.</returns>
        /// <exception cref="InvalidOperationException">Thrown when <see cref="CanRead"/> returns <c>false</c>.</exception>
        /// <exception cref="System.IO.EndOfStreamException">Thrown if the transport ends while reading a message.</exception>
        /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is canceled before a new message is received.</exception>
        /// <remarks>
        /// Implementations may assume this method is never called before any async result
        /// from a prior call to this method has completed.
        /// </remarks>
        ValueTask<JToken> ReadAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Writes a JSON-RPC message to the transport and flushes.
        /// </summary>
        /// <param name="json">The JSON token to write.</param>
        /// <param name="cancellationToken">A token to cancel the write request.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when <see cref="CanWrite"/> returns <c>false</c>.</exception>
        /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is canceled before message transmission begins.</exception>
        /// <remarks>
        /// Implementations should expect this method to be invoked concurrently
        /// and use a queue to preserve message order as they are transmitted one at a time.
        /// </remarks>
        ValueTask WriteAsync(JToken json, CancellationToken cancellationToken);
    }
}
