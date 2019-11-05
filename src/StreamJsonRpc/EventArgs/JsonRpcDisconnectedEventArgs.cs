// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using Microsoft;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Describes the reason behind a disconnection with the remote party.
    /// </summary>
    /// <seealso cref="System.EventArgs" />
    public class JsonRpcDisconnectedEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcDisconnectedEventArgs"/> class.
        /// </summary>
        /// <param name="description">The description.</param>
        /// <param name="reason">The reason for disconnection.</param>
        public JsonRpcDisconnectedEventArgs(string description, DisconnectedReason reason)
            : this(description, reason, lastMessage: null, exception: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcDisconnectedEventArgs"/> class.
        /// </summary>
        /// <param name="description">The description.</param>
        /// <param name="reason">The reason for disconnection.</param>
        /// <param name="exception">The exception.</param>
        public JsonRpcDisconnectedEventArgs(string description, DisconnectedReason reason, Exception? exception)
            : this(description, reason, lastMessage: null, exception: exception)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcDisconnectedEventArgs"/> class.
        /// </summary>
        /// <param name="description">The description.</param>
        /// <param name="reason">The reason for disconnection.</param>
        /// <param name="lastMessage">The last message.</param>
        public JsonRpcDisconnectedEventArgs(string description, DisconnectedReason reason, JToken? lastMessage)
            : this(description, reason, lastMessage: lastMessage, exception: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonRpcDisconnectedEventArgs"/> class.
        /// </summary>
        /// <param name="description">The description.</param>
        /// <param name="reason">The reason for disconnection.</param>
        /// <param name="lastMessage">The last message.</param>
        /// <param name="exception">The exception.</param>
        public JsonRpcDisconnectedEventArgs(string description, DisconnectedReason reason, JToken? lastMessage, Exception? exception)
        {
            Requires.NotNullOrWhiteSpace(description, nameof(description));

            this.Description = description;
            this.Reason = reason;
            this.LastMessage = lastMessage;
            this.Exception = exception;
        }

        /// <summary>
        /// Gets the description.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets the reason.
        /// </summary>
        public DisconnectedReason Reason { get; }

        /// <summary>
        /// Gets the last message.
        /// </summary>
        public JToken? LastMessage { get; }

        /// <summary>
        /// Gets the exception.
        /// </summary>
        public Exception? Exception { get; }
    }
}
