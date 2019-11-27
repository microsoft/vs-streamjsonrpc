// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using System;
    using Microsoft;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Carries the <see cref="RequestId"/> from request or response messages.
    /// </summary>
    public class JsonRpcMessageEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcMessageEventArgs"/> class.
        /// </summary>
        /// <param name="requestId">The ID from the request or response that the event is regarding.</param>
        public JsonRpcMessageEventArgs(RequestId requestId)
        {
            Requires.Argument(!requestId.IsEmpty, nameof(requestId), "Non-default ID required.");
            this.RequestId = requestId;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcMessageEventArgs"/> class.
        /// </summary>
        /// <param name="message">The message the event is regarding.</param>
        internal JsonRpcMessageEventArgs(IJsonRpcMessageWithId message)
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
