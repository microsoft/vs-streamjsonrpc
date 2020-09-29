// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    /// <summary>
    /// Interface optionally implemented by <see cref="IJsonRpcMessageFormatter" /> implementations that need a reference to their owner <see cref="JsonRpc" /> class.
    /// </summary>
    public interface IJsonRpcInstanceContainer
    {
        /// <summary>
        /// Sets the <see cref="JsonRpc"/> instance.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">May be thrown when set more than once.</exception>
#pragma warning disable CA1044 // Properties should not be write only
        JsonRpc Rpc
#pragma warning restore CA1044 // Properties should not be write only
        {
            set;
        }
    }
}
