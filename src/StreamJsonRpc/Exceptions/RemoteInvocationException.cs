using System;

namespace StreamJsonRpc
{
    /// <summary>
    /// Remote RPC exception that indicates that the server target method threw an exception.
    /// </summary>
    /// <remarks>
    /// The details of the target method exception can be found on <see cref="RemoteStackTrace"/> and <see cref="RemoteErrorCode"/>.
    /// </remarks>
#if NET45
    [System.Serializable]
#endif
    public class RemoteInvocationException : RemoteRpcException
    {
        internal RemoteInvocationException(string message) : base(message)
        {
        }

        public  RemoteInvocationException(string message, string remoteStack, string remoteCode) : this(message)
        {
            this.RemoteStackTrace = remoteStack;
            this.RemoteErrorCode = remoteCode;
        }

#if NET45
        protected RemoteInvocationException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context)
        { }
#endif

        public string RemoteStackTrace { get; }

        public string RemoteErrorCode { get; }
    }
}
