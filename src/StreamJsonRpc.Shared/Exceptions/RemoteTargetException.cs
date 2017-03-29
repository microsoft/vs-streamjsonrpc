using System;

namespace StreamJsonRpc
{
    /// <summary>
    /// Remote RPC exception that indicates that target object is invalid.
    /// </summary>
#if DESKTOP
    [System.Serializable]
#endif
    public class RemoteTargetException : RemoteRpcException
    {
        internal RemoteTargetException(string message) : base(message)
        {
        }

#if DESKTOP
        protected RemoteTargetException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context)
        { }
#endif

    }
}
