// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Implemented by proxies returned from <see cref="JsonRpc.Attach{T}(IJsonRpcMessageHandler, JsonRpcProxyOptions)"/> and its overloads
/// or other libraries that create "local" proxies that wish to offer similar functionality to a remote proxy so that callers
/// can treat the proxies the same whether the service is remote or local.
/// </summary>
public interface IClientProxy
{
    /// <summary>
    /// Gets a value indicating whether a given interface was requested for this proxy
    /// explicitly (as opposed to being included as an artifact of its implementation).
    /// </summary>
    /// <param name="type">A contract interface type.</param>
    /// <returns><see langword="true" /> if the proxy was created with the specified <paramref name="type"/> expressly listed as needing to be implemented in the proxy; otherwise <see langword="false" />.</returns>
    bool Is(Type type);
}
