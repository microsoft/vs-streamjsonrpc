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
        /// An error occurred while accessing the stream.
        /// </summary>
        StreamError,

        /// <summary>
        /// A syntax or schema error while reading a JSON-RPC packet occurred.
        /// </summary>
        ParseError,

        /// <summary>
        /// The <see cref="JsonRpc"/> instance was disposed.
        /// </summary>
        LocallyDisposed,

        /// <summary>
        /// The underlying transport was closed by the remote party.
        /// </summary>
        RemotePartyTerminated,

        /// <summary>
        /// A fatal exception was thrown in a local method that was requested by the remote party.
        /// </summary>
        FatalException,

        /// <summary>
        /// An extensibility point was leveraged locally and broke the contract.
        /// </summary>
        LocalContractViolation,

        /// <summary>
        /// The remote party violated the JSON-RPC protocol.
        /// </summary>
        RemoteProtocolViolation,
    }
}
