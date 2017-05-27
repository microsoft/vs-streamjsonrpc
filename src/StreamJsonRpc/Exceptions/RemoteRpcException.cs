using System;

namespace StreamJsonRpc
{
    /// <summary>
    /// Base exception class for any exception that happens while receiving an JSON RPC communication.
    /// </summary>
#if NET45
    [System.Serializable]
#endif
    public abstract class RemoteRpcException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteRpcException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        protected RemoteRpcException(string message) : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteRpcException"/> class.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference (Nothing in Visual Basic) if no inner exception is specified.</param>
        protected RemoteRpcException(string message, Exception innerException) : base(message, innerException)
        {
        }

#if NET45
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteRpcException"/> class.
        /// </summary>
        /// <param name="info">Serialization info.</param>
        /// <param name="context">Streaming context.</param>
        protected RemoteRpcException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context)
        { }
#endif
    }
}
