// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using Microsoft;

    /// <summary>
    /// Remote RPC exception that indicates that the requested target method was not found on the server.
    /// </summary>
    /// <remarks>
    /// Check the exception message for the reasons why the method was not found. It's possible that
    /// there was a method with the matching name, but it was not public, had ref or out params, or
    /// its arguments were incompatible with the arguments supplied by the client.
    /// </remarks>
    [System.Serializable]
    public class RemoteMethodNotFoundException : RemoteRpcException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteMethodNotFoundException"/> class
        /// with supplied message and target method.
        /// </summary>
        /// <param name="message">Exception message describing why the method was not found.</param>
        /// <param name="targetMethod">Target method that was not found.</param>
        internal RemoteMethodNotFoundException(string message, string targetMethod)
            : base(message)
        {
            Requires.NotNullOrEmpty(targetMethod, nameof(targetMethod));
            this.TargetMethod = targetMethod;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteMethodNotFoundException"/> class.
        /// </summary>
        /// <param name="info">Serialization info.</param>
        /// <param name="context">Streaming context.</param>
        protected RemoteMethodNotFoundException(
             System.Runtime.Serialization.SerializationInfo info,
             System.Runtime.Serialization.StreamingContext context)
            : base(info, context)
        {
        }

        /// <summary>
        /// Gets the name of the target method that was not found.
        /// </summary>
        public string TargetMethod { get; }
    }
}
