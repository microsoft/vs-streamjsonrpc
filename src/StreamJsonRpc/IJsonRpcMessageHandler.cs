﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using StreamJsonRpc.Protocol;

namespace StreamJsonRpc;

/// <summary>
/// The contract for sending and receiving JSON-RPC messages.
/// </summary>
public interface IJsonRpcMessageHandler
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
    /// Gets the formatter used for message serialization.
    /// </summary>
    IJsonRpcMessageFormatter Formatter { get; }

    /// <summary>
    /// Reads a distinct and complete message from the transport, waiting for one if necessary.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the read request.</param>
    /// <returns>The received message, or <see langword="null"/> if the underlying transport ends before beginning another message.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="CanRead"/> returns <see langword="false"/>.</exception>
    /// <exception cref="System.IO.EndOfStreamException">Thrown if the transport ends while reading a message.</exception>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is canceled before a new message is received.</exception>
    /// <remarks>
    /// Implementations may assume this method is never called before any async result
    /// from a prior call to this method has completed.
    /// </remarks>
    ValueTask<JsonRpcMessage?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes a JSON-RPC message to the transport and flushes.
    /// </summary>
    /// <param name="jsonRpcMessage">The message to write.</param>
    /// <param name="cancellationToken">A token to cancel the write request.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="CanWrite"/> returns <see langword="false"/>.</exception>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is canceled before message transmission begins.</exception>
    /// <remarks>
    /// Implementations should expect this method to be invoked concurrently
    /// and use a queue to preserve message order as they are transmitted one at a time.
    /// </remarks>
    ValueTask WriteAsync(JsonRpcMessage jsonRpcMessage, CancellationToken cancellationToken);
}
