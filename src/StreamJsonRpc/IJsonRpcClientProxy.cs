// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Implemented by proxies returned from <see cref="JsonRpc.Attach{T}(IJsonRpcMessageHandler, JsonRpcProxyOptions)"/> and its overloads
/// to provide access to additional JSON-RPC functionality.
/// </summary>
public interface IJsonRpcClientProxy : IDisposable
{
    /// <summary>
    /// Gets the <see cref="StreamJsonRpc.JsonRpc"/> instance behind this proxy.
    /// </summary>
    JsonRpc JsonRpc { get; }

    /// <summary>
    /// Gets a value indicating whether a given interface was requested for this proxy
    /// explicitly (as opposed to being included as an artifact of its implementation).
    /// </summary>
    /// <param name="contract">An RPC contract interface type.</param>
    /// <returns><see langword="true" /> if the proxy creator asked for <paramref name="contract"/>; otherwise <see langword="false" />.</returns>
    /// <remarks>
    /// Typically a simple conditional cast would be sufficient to determine whether a proxy implements a given interface.
    /// However when <see cref="JsonRpcProxyOptions.AcceptProxyWithExtraInterfaces"/> is <see langword="true"/> a proxy may be returned
    /// that implements extra interfaces.
    /// In such cases, this method can be used to determine whether the proxy was intentionally created to implement the interface
    /// or not, allowing feature testing to still happen since conditional casting might lead to false positives.
    /// </remarks>
    bool IsInterfaceIntentionallyImplemented(Type contract);
}
