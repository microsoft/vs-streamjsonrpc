// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;

    /// <summary>
    /// Remote RPC exception that indicates that the server target method threw an exception.
    /// </summary>
    /// <remarks>
    /// The details of the target method exception can be found on <see cref="RemoteStackTrace"/> and <see cref="RemoteErrorCode"/>.
    /// </remarks>
#if SERIALIZABLE_EXCEPTIONS
    [System.Serializable]
#endif
    public class RemoteInvocationException : RemoteRpcException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteInvocationException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="remoteStack">The remote stack.</param>
        /// <param name="remoteCode">The remote code.</param>
        public RemoteInvocationException(string message, string remoteStack, string remoteCode)
            : this(message)
        {
            this.RemoteStackTrace = remoteStack;
            this.RemoteErrorCode = remoteCode;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteInvocationException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        internal RemoteInvocationException(string message)
            : base(message)
        {
        }

#if SERIALIZABLE_EXCEPTIONS
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteInvocationException"/> class.
        /// </summary>
        /// <param name="info">Serialization info.</param>
        /// <param name="context">Streaming context.</param>
        protected RemoteInvocationException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context)
            : base(info, context)
        {
        }
#endif

        /// <summary>
        /// Gets the stack trace for the remote exception.
        /// </summary>
        public string RemoteStackTrace { get; }

        /// <summary>
        /// Gets the remote error code.
        /// </summary>
        public string RemoteErrorCode { get; }
    }
}
