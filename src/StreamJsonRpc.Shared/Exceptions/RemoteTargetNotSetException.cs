using System;

namespace StreamJsonRpc
{
    /// <summary>
    /// Remote RPC exception that indicates that the server has no target object.
    /// </summary>
#if DESKTOP
    [System.Serializable]
#endif
    public class RemoteTargetNotSetException : RemoteRpcException
    {
        internal RemoteTargetNotSetException(string message) : base(message)
        {
        }

#if DESKTOP
        protected RemoteTargetNotSetException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context)
        { }
#endif

    }
}
