// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Text;
    using Microsoft;
    using StreamJsonRpc.Protocol;

    /// <summary>
    /// An exception that may be thrown within a locally invoked server method, and carries with it data that influences the JSON-RPC error message's error object.
    /// </summary>
    public class LocalRpcException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LocalRpcException"/> class.
        /// </summary>
        public LocalRpcException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalRpcException"/> class.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        public LocalRpcException(string? message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalRpcException"/> class.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="inner">The exception that is the cause of the current exception, or a null reference (Nothing in Visual Basic) if no inner exception is specified.</param>
        public LocalRpcException(string? message, Exception? inner)
                : base(message, inner)
        {
        }

        /// <summary>
        /// Gets or sets the value for the error.data property.
        /// </summary>
        public object? ErrorData { get; set; }

        /// <summary>
        /// Gets or sets the value for the error.code property.
        /// </summary>
        /// <remarks>
        /// The default value is set to a special general error code: <see cref="JsonRpcErrorCode.InvocationError"/>.
        /// This may be set to a more meaningful error code for the application that allows the client to programatically respond to the error condition.
        /// Application-defined values should avoid the [-32768, -32000] range, which is reserved for the JSON-RPC protocol itself.
        /// </remarks>
        public int ErrorCode { get; set; } = (int)JsonRpcErrorCode.InvocationError;
    }
}
