// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    /// <summary>
    /// Identifies a reason for a stream disconnection.
    /// </summary>
    public enum DisconnectedReason
    {
        /// <summary>
        /// Unknown reason.
        /// </summary>
        Unknown,

        /// <summary>
        /// An error occurred while accessing the stream.
        /// </summary>
        StreamError,

        /// <summary>
        /// A syntax or schema error while reading a JSON-RPC packet occurred.
        /// </summary>
        ParseError,

        /// <summary>
        /// The stream was disposed.
        /// </summary>
        Disposed,
    }
}
