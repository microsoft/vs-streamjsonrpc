// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using System;
    using Microsoft;
    using StreamJsonRpc.Protocol;

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
        event EventHandler<JsonRpcFormatterCallbackEventArgs> RequestTransmissionAborted;

        /// <summary>
        /// Occurs when <see cref="JsonRpc"/> receives a successful response to a previously sent request.
        /// </summary>
        event EventHandler<JsonRpcFormatterCallbackEventArgs> ResultReceived;

        /// <summary>
        /// Occurs when <see cref="JsonRpc"/> receives an error response to a previously sent request.
        /// </summary>
        event EventHandler<JsonRpcFormatterCallbackEventArgs> ErrorReceived;
    }

    /// <summary>
    /// The data passed to <see cref="IJsonRpcFormatterCallbacks"/> event handlers.
    /// </summary>
    public class JsonRpcFormatterCallbackEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcFormatterCallbackEventArgs"/> class.
        /// </summary>
        /// <param name="requestId">The ID from the request or response that the event is regarding.</param>
        public JsonRpcFormatterCallbackEventArgs(RequestId requestId)
        {
            Requires.Argument(!requestId.IsEmpty, nameof(requestId), "Non-default ID required.");
            this.RequestId = requestId;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcFormatterCallbackEventArgs"/> class.
        /// </summary>
        /// <param name="message">The message the event is regarding.</param>
        internal JsonRpcFormatterCallbackEventArgs(IJsonRpcMessageWithId message)
        {
            Requires.NotNull(message, nameof(message));
            Requires.Argument(!message.RequestId.IsEmpty, nameof(message), "Non-default ID required.");
            this.RequestId = message.RequestId;
        }

        /// <summary>
        /// Gets the id on the request, result or error.
        /// </summary>
        public RequestId RequestId { get; private set; }
    }
}
