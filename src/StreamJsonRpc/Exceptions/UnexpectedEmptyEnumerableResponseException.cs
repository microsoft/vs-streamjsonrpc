// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System;

    internal class UnexpectedEmptyEnumerableResponseException : RemoteRpcException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UnexpectedEmptyEnumerableResponseException"/> class.
        /// </summary>
        /// <inheritdoc cref="RemoteRpcException(string)"/>
        public UnexpectedEmptyEnumerableResponseException(string? message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UnexpectedEmptyEnumerableResponseException"/> class.
        /// </summary>
        /// <inheritdoc cref="RemoteRpcException(string, Exception)"/>
        public UnexpectedEmptyEnumerableResponseException(string? message, Exception? innerException)
            : base(message, innerException)
        {
        }
    }
}
