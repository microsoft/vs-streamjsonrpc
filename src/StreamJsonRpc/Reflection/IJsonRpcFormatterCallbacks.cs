// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using System;

    /// <summary>
    /// Implemented by <see cref="JsonRpc"/> to expose callbacks allowing an <see cref="IJsonRpcMessageFormatter"/> to perform resource cleanup.
    /// </summary>
    public interface IJsonRpcFormatterCallbacks
    {
        /// <summary>
        /// Occurs when <see cref="JsonRpc"/> aborts the transmission of an outbound request (that was not a notification).
        /// </summary>
        /// <remarks>
        /// This usually occurs because of an exception during serialization or transmission, possibly due to cancellation.
        /// </remarks>
        event EventHandler<JsonRpcMessageEventArgs> RequestTransmissionAborted;

        /// <summary>
        /// Occurs when <see cref="JsonRpc"/> receives a response to a previously sent request.
        /// </summary>
        event EventHandler<JsonRpcResponseEventArgs> ResponseReceived;

        /// <summary>
        /// Occurs when <see cref="JsonRpc"/> transmits a response message.
        /// </summary>
        event EventHandler<JsonRpcResponseEventArgs> ResponseSent;
    }
}
