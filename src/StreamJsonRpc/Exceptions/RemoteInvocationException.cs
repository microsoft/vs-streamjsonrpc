// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using Newtonsoft.Json.Linq;

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
            : this(message, remoteStack, remoteCode, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteInvocationException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="remoteStack">The remote stack.</param>
        /// <param name="remoteCode">The remote code.</param>
        /// <param name="errorData">The value of the error.data field in the response.</param>
        public RemoteInvocationException(string message, string remoteStack, string remoteCode, object errorData)
            : base(message)
        {
            this.RemoteStackTrace = remoteStack;
            this.RemoteErrorCode = remoteCode;
            this.ErrorData = errorData;
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
        /// Gets the value of the <c>error.data.stack</c> field in the response, if that value is a string.
        /// </summary>
        public string RemoteStackTrace { get; }

        /// <summary>
        /// Gets the value of the <c>error.data.code</c> field in the response, if that value is a string or integer.
        /// </summary>
        public string RemoteErrorCode { get; }

        /// <summary>
        /// Gets the <c>error.data</c> value in the error response, if one was provided.
        /// </summary>
        public object ErrorData { get; }
    }
}
