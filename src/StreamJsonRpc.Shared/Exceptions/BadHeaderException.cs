using System;

namespace StreamJsonRpc
{
#if DESKTOP
    [Serializable]
#endif
    public class BadHeaderException : RemoteRpcException
    {
        internal BadHeaderException(string message) : base(message)
        {
        }

#if DESKTOP
        protected BadHeaderException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context)
        { }
#endif
    }
}
