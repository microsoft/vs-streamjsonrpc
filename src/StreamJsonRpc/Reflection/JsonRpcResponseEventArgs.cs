// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Reflection
{
    using System;
    using Microsoft;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Carries the <see cref="RequestId"/> and success status of response messages.
    /// </summary>
    public class JsonRpcResponseEventArgs : JsonRpcMessageEventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcResponseEventArgs"/> class.
        /// </summary>
        /// <param name="requestId">The ID from the request or response that the event is regarding.</param>
        /// <param name="isSuccessfulResponse">A flag indicating whether the response is a result (as opposed to an error).</param>
        public JsonRpcResponseEventArgs(RequestId requestId, bool isSuccessfulResponse)
            : base(requestId)
        {
            this.IsSuccessfulResponse = isSuccessfulResponse;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcResponseEventArgs"/> class.
        /// </summary>
        /// <param name="message">The message the event is regarding.</param>
        internal JsonRpcResponseEventArgs(IJsonRpcMessageWithId message)
            : this(message.RequestId, message is JsonRpcResult)
        {
        }

        /// <summary>
        /// Gets a value indicating whether the response is a result (as opposed to an error).
        /// </summary>
        public bool IsSuccessfulResponse { get; }
    }
}
