// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;

    /// <summary>
    /// Remote RPC exception that indicates that the server has no target object.
    /// </summary>
    /// <seealso cref="RemoteRpcException" />
#if SERIALIZABLE_EXCEPTIONS
    [System.Serializable]
#endif
    public class RemoteTargetNotSetException : RemoteRpcException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteTargetNotSetException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        internal RemoteTargetNotSetException(string message)
            : base(message)
        {
        }

#if SERIALIZABLE_EXCEPTIONS
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteTargetNotSetException"/> class.
        /// </summary>
        /// <param name="info">Serialization info.</param>
        /// <param name="context">Streaming context.</param>
        protected RemoteTargetNotSetException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context)
            : base(info, context)
        {
        }
#endif

    }
}
