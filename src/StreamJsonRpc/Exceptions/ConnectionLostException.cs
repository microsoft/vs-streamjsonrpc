// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// An exception used to fault a <see cref="Task"/> returned from a <see cref="JsonRpc"/> request
    /// when the request could not be completed or the response cannot be received because the connection dropped.
    /// </summary>
    [System.Serializable]
    public class ConnectionLostException : RemoteRpcException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionLostException"/> class.
        /// </summary>
        public ConnectionLostException()
            : this(Resources.ConnectionDropped)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionLostException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public ConnectionLostException(string? message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionLostException"/> class.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference (Nothing in Visual Basic) if no inner exception is specified.</param>
        public ConnectionLostException(string? message, Exception? innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionLostException"/> class.
        /// </summary>
        /// <param name="info">Serialization info.</param>
        /// <param name="context">Streaming context.</param>
        protected ConnectionLostException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context)
            : base(info, context)
        {
        }
    }
}
