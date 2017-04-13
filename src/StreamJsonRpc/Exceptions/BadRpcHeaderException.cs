using System;

namespace StreamJsonRpc
{
#if NET45
    [Serializable]
#endif
    public class BadRpcHeaderException : RemoteRpcException
    {
        internal BadRpcHeaderException(string message) : base(message)
        {
        }

        internal BadRpcHeaderException(string message, Exception innerException) : base(message, innerException)
        {
        }

#if NET45
        protected BadRpcHeaderException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context)
        { }
#endif
    }
}
