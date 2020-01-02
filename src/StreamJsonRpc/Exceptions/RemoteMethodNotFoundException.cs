// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Runtime.Serialization;
    using Microsoft;
    using Newtonsoft.Json.Linq;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Remote RPC exception that indicates that the requested target method was not found on the server.
    /// </summary>
    /// <remarks>
    /// Check the exception message for the reasons why the method was not found. It's possible that
    /// there was a method with the matching name, but it was not public, had ref or out params, or
    /// its arguments were incompatible with the arguments supplied by the client.
    /// </remarks>
    [Serializable]
    public class RemoteMethodNotFoundException : RemoteRpcException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteMethodNotFoundException"/> class
        /// with supplied message and target method.
        /// </summary>
        /// <param name="message">Exception message describing why the method was not found.</param>
        /// <param name="targetMethod">Target method that was not found.</param>
        /// <param name="errorCode">The value of the error.code field in the response.</param>
        /// <param name="errorData">The value of the error.data field in the response.</param>
        /// <param name="deserializedErrorData">The value of the error.data field in the response, deserialized according to <see cref="JsonRpc.GetErrorDetailsDataType(JsonRpcError)"/>.</param>
        internal RemoteMethodNotFoundException(string? message, string targetMethod, JsonRpcErrorCode errorCode, object? errorData, object? deserializedErrorData)
            : base(message)
        {
            Requires.NotNullOrEmpty(targetMethod, nameof(targetMethod));
            this.ErrorCode = errorCode;
            this.TargetMethod = targetMethod;
            this.ErrorData = errorData;
            this.DeserializedErrorData = deserializedErrorData;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteMethodNotFoundException"/> class.
        /// </summary>
        /// <param name="info">Serialization info.</param>
        /// <param name="context">Streaming context.</param>
        protected RemoteMethodNotFoundException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.TargetMethod = info.GetString(nameof(this.TargetMethod))!;
        }

        /// <summary>
        /// Gets the name of the target method that was not found.
        /// </summary>
        public string TargetMethod { get; }

        /// <summary>
        /// Gets the value of the <c>error.code</c> field in the response.
        /// </summary>
        /// <value>
        /// The value is typically either <see cref="JsonRpcErrorCode.InvalidParams"/> or <see cref="JsonRpcErrorCode.MethodNotFound"/>.
        /// </value>
        public JsonRpcErrorCode ErrorCode { get; }

        /// <summary>
        /// Gets the <c>error.data</c> value in the error response, if one was provided.
        /// </summary>
        /// <remarks>
        /// Depending on the <see cref="IJsonRpcMessageFormatter"/> used, the value of this property, if any,
        /// may be a <see cref="JToken"/> or a deserialized object.
        /// If a deserialized object, the type of this object is determined by <see cref="JsonRpc.GetErrorDetailsDataType(JsonRpcError)"/>.
        /// The default implementation of this method produces a <see cref="CommonErrorData"/> object.
        /// </remarks>
        public object? ErrorData { get; }

        /// <summary>
        /// Gets the <c>error.data</c> value in the error response, if one was provided.
        /// </summary>
        /// <remarks>
        /// The type of this object is determined by <see cref="JsonRpc.GetErrorDetailsDataType(JsonRpcError)"/>.
        /// The default implementation of this method produces a <see cref="CommonErrorData"/> object.
        /// </remarks>
        public object? DeserializedErrorData { get; }

        /// <inheritdoc/>
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);

            info.AddValue(nameof(this.TargetMethod), this.TargetMethod);
        }
    }
}
