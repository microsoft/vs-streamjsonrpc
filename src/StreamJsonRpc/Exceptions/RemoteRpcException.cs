// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Runtime.Serialization;
    using Newtonsoft.Json.Linq;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Base exception class for any exception that happens while receiving an JSON-RPC communication.
    /// </summary>
    [System.Serializable]
#pragma warning disable CA1032 // Implement standard exception constructors
    public abstract class RemoteRpcException : Exception
#pragma warning restore CA1032 // Implement standard exception constructors
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteRpcException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        protected RemoteRpcException(string? message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteRpcException"/> class.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference (Nothing in Visual Basic) if no inner exception is specified.</param>
        protected RemoteRpcException(string? message, Exception? innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteRpcException"/> class.
        /// </summary>
        /// <param name="info">Serialization info.</param>
        /// <param name="context">Streaming context.</param>
        protected RemoteRpcException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.ErrorCode = (JsonRpcErrorCode?)(int?)info.GetValue(nameof(this.ErrorCode), typeof(int?));
        }

        /// <summary>
        /// Gets or sets the value of the <c>error.code</c> field in the response, if one is available.
        /// </summary>
        public JsonRpcErrorCode? ErrorCode { get; protected set; }

        /// <summary>
        /// Gets or sets the <c>error.data</c> value in the error response, if one was provided.
        /// </summary>
        /// <remarks>
        /// Depending on the <see cref="IJsonRpcMessageFormatter"/> used, the value of this property, if any,
        /// may be a <see cref="JToken"/> or a deserialized object.
        /// If a deserialized object, the type of this object is determined by <see cref="JsonRpc.GetErrorDetailsDataType(JsonRpcError)"/>.
        /// The default implementation of this method produces a <see cref="CommonErrorData"/> object.
        /// </remarks>
        public object? ErrorData { get; protected set; }

        /// <summary>
        /// Gets or sets the <c>error.data</c> value in the error response, if one was provided.
        /// </summary>
        /// <remarks>
        /// The type of this object is determined by <see cref="JsonRpc.GetErrorDetailsDataType(JsonRpcError)"/>.
        /// The default implementation of this method produces a <see cref="CommonErrorData"/> object.
        /// </remarks>
        public object? DeserializedErrorData { get; protected set; }

        /// <inheritdoc/>
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);

            if (this.ErrorCode.HasValue)
            {
                info.AddValue(nameof(this.ErrorCode), (int)this.ErrorCode.Value);
            }
            else
            {
                info.AddValue(nameof(this.ErrorCode), null);
            }
        }
    }
}
