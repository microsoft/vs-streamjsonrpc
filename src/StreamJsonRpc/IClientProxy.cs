// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Indicates whether an RPC marshaled object implements an interface.
/// </summary>
/// <remarks>
/// <para>
/// When casting or type checking between two <see cref="RpcMarshalableAttribute"/>-annotated interfaces,
/// it is preferable to use the <see cref="Is(Type)" /> or <see cref="JsonRpcExtensions.As{T}" /> methods rather than a direct cast or traditional type check.
/// This is because the interface may be implemented by a proxy object that implements more interfaces than the marshaled object actually implements.
/// Using these methods informs the caller as to the actual interfaces that are supported by the remote object.
/// </para>
/// <para>
/// This interface is implemented by RPC proxies returned from <see cref="JsonRpc.Attach{T}(IJsonRpcMessageHandler, JsonRpcProxyOptions)"/> and its overloads
/// or as RPC marshalable objects.
/// If proxies are generated for these interfaces by any other system, that proxy should also implement this interface to participate in similar dynamic type checking.
/// </para>
/// </remarks>
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
