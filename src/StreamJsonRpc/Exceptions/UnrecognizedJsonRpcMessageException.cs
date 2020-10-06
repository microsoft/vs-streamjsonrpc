// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Runtime.Serialization;

    /// <summary>
    /// An exception thrown when an incoming JSON-RPC message could not be recognized as conforming to any known JSON-RPC message.
    /// </summary>
    [Serializable]
    public class UnrecognizedJsonRpcMessageException : RemoteRpcException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UnrecognizedJsonRpcMessageException"/> class.
        /// </summary>
        public UnrecognizedJsonRpcMessageException()
            : base(Resources.UnrecognizableMessage)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UnrecognizedJsonRpcMessageException"/> class.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        public UnrecognizedJsonRpcMessageException(string? message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UnrecognizedJsonRpcMessageException"/> class.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference (Nothing in Visual Basic) if no inner exception is specified.</param>
        public UnrecognizedJsonRpcMessageException(string? message, Exception? innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UnrecognizedJsonRpcMessageException"/> class.
        /// </summary>
        /// <param name="info">Serialization info.</param>
        /// <param name="context">Streaming context.</param>
        protected UnrecognizedJsonRpcMessageException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
