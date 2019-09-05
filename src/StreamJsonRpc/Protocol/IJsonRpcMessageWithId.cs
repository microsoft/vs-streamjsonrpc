// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc.Protocol
{
    internal interface IJsonRpcMessageWithId
    {
        /// <summary>
        /// Gets the ID from a message.
        /// </summary>
        object Id { get; set; }
    }
}
