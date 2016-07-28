using System;

namespace StreamJsonRpc
{
    /// <summary>
    /// Base exception class for any exception that happens while receiving an JSON RPC communication.
    /// </summary>
#if DESKTOP
    [System.Serializable]
#endif
    public abstract class RemoteRpcException : Exception
    {
        protected RemoteRpcException(string message) : base(message)
        {
        }

        protected RemoteRpcException(string message, Exception innerException) : base(message, innerException)
        {
        }

#if DESKTOP
        protected RemoteRpcException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context)
        { }
#endif
    }
}
