using System;
using Microsoft;

namespace StreamJsonRpc
{
    /// <summary>
    /// Remote RPC exception that indicates that the requested target method was not found on the server.
    /// </summary>
    /// <remarks>
    /// Check the exception message for the reasons why the method was not found. It's possible that
    /// there was a method with the matching name, but it was not public, had ref or out params, or
    /// its arguments were incompatible with the arguments supplied by the client.
    /// </remarks>
#if NET45
    [System.Serializable]
#endif
    public class RemoteMethodNotFoundException : RemoteRpcException
    {
        /// <summary>
        /// Initializes a new instance of <see cref="RemoteMethodNotFoundException"/> with supplied message and target method.
        /// </summary>
        /// <param name="message">Exception message describing why the method was not found.</param>
        /// <param name="targetMethod">Target method that was not found.</param>
        internal RemoteMethodNotFoundException(string message, string targetMethod) : base(message)
        {
            Requires.NotNullOrEmpty(targetMethod, nameof(targetMethod));
            this.TargetMethod = targetMethod;
        }

#if NET45
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteMethodNotFoundException"/> class.
        /// </summary>
        /// <param name="info">Serialization info.</param>
        /// <param name="context">Streaming context.</param>
        protected RemoteMethodNotFoundException(
             System.Runtime.Serialization.SerializationInfo info,
             System.Runtime.Serialization.StreamingContext context) : base(info, context)
        { }
#endif

        /// <summary>
        /// Gets the name of the target method that was not found.
        /// </summary>
        public string TargetMethod { get; }
    }
}
