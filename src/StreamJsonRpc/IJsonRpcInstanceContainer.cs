// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    /// <summary>
    /// Interface to contain an instance of <see cref="JsonRpc"/>.
    /// </summary>
    public interface IJsonRpcInstanceContainer
    {
        /// <summary>
        /// Gets or sets the <see cref="JsonRpc"/> instance.
        /// </summary>
        JsonRpc Rpc
        {
            set;
        }
    }
}
