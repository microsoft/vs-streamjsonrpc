// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An exception thrown from back to the client from various <see cref="JsonRpc"/> request methods when the server failed to serialize the response.
    /// </summary>
    /// <remarks>
    /// This exception comes from the <see cref="JsonRpcErrorCode.ResponseSerializationFailure"/> error code.
    /// </remarks>
    public class RemoteSerializationException : RemoteRpcException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteSerializationException"/> class.
        /// </summary>
        /// <inheritdoc cref="RemoteRpcException(string?)"/>
        public RemoteSerializationException(string? message, object? errorData, object? deserializedErrorData)
            : base(message)
        {
            this.ErrorCode = JsonRpcErrorCode.ResponseSerializationFailure;
            this.ErrorData = errorData;
            this.DeserializedErrorData = deserializedErrorData;
        }
    }
}
