// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.IO;
    using System.Runtime.Serialization;
    using System.Text;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// Remote RPC exception that indicates that the server target method threw an exception.
    /// </summary>
    /// <remarks>
    /// The details of the target method exception can be found on the <see cref="ErrorCode"/> and <see cref="ErrorData"/> properties.
    /// </remarks>
    [Serializable]
#pragma warning disable CA1032 // Implement standard exception constructors
    public class RemoteInvocationException : RemoteRpcException
#pragma warning restore CA1032 // Implement standard exception constructors
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteInvocationException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="errorCode">The value of the error.code field in the response.</param>
        /// <param name="errorData">The value of the error.data field in the response.</param>
        public RemoteInvocationException(string? message, int errorCode, object? errorData)
            : base(message)
        {
            base.ErrorCode = (JsonRpcErrorCode)errorCode;
            base.ErrorData = errorData;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteInvocationException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="errorCode">The value of the error.code field in the response.</param>
        /// <param name="errorData">The value of the error.data field in the response.</param>
        /// <param name="deserializedErrorData">The value of the error.data field in the response, deserialized according to <see cref="JsonRpc.GetErrorDetailsDataType(JsonRpcError)"/>.</param>
        public RemoteInvocationException(string? message, int errorCode, object? errorData, object? deserializedErrorData)
            : base(message)
        {
            base.ErrorCode = (JsonRpcErrorCode)errorCode;
            base.ErrorData = errorData;
            base.DeserializedErrorData = deserializedErrorData;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteInvocationException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="errorCode">The value of the error.code field in the response.</param>
        /// <param name="innerException">The deserialized exception thrown by the RPC server.</param>
        public RemoteInvocationException(string? message, int errorCode, Exception innerException)
            : base(message, innerException)
        {
            base.ErrorCode = (JsonRpcErrorCode)errorCode;

            // Emulate the CommonErrorData object tree as well so folks can keep reading that if they were before.
            base.DeserializedErrorData = new CommonErrorData(innerException);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteInvocationException"/> class.
        /// </summary>
        /// <param name="info">Serialization info.</param>
        /// <param name="context">Streaming context.</param>
        protected RemoteInvocationException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }

        /// <summary>
        /// Gets the value of the <c>error.code</c> field in the response.
        /// </summary>
        /// <value>
        /// The value may be any integer.
        /// The value may be <see cref="JsonRpcErrorCode.InvocationError"/>, which is a general value used for exceptions thrown on the server when the server does not give an app-specific error code.
        /// </value>
        public new int ErrorCode => (int)base.ErrorCode!.Value;

        /// <inheritdoc cref="RemoteRpcException.ErrorData" />
        public new object? ErrorData => base.ErrorData;

        /// <inheritdoc cref="RemoteRpcException.DeserializedErrorData" />
        public new object? DeserializedErrorData => base.DeserializedErrorData;

        /// <inheritdoc/>
        public override string ToString()
        {
            string result = base.ToString();

            if (this.DeserializedErrorData is CommonErrorData errorData)
            {
                var builder = new StringBuilder(result);
                builder.AppendLine();
                builder.AppendLine("RPC server exception:");
                builder.AppendLine($"{errorData.TypeName}: {errorData.Message}");
                if (errorData.Inner is object)
                {
                    ContributeInnerExceptionDetails(builder, errorData.Inner);
                }

                ContributeStackTrace(builder, errorData);
                result = builder.ToString();
            }

            return result;
        }

        private static void ContributeInnerExceptionDetails(StringBuilder builder, CommonErrorData errorData)
        {
            builder.AppendLine($" ---> {errorData.TypeName}: {errorData.Message}");
            if (errorData.Inner is object)
            {
                ContributeInnerExceptionDetails(builder, errorData.Inner);
            }

            ContributeStackTrace(builder, errorData);

            builder.AppendLine("   --- End of inner exception stack trace ---");
        }

        private static void ContributeStackTrace(StringBuilder builder, CommonErrorData errorData)
        {
            if (errorData.StackTrace != null)
            {
                using (var sr = new StringReader(errorData.StackTrace))
                {
                    while (sr.ReadLine() is string line)
                    {
                        builder.AppendLine($"   {line}");
                    }
                }
            }
        }
    }
}
