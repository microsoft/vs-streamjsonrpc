// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace StreamJsonRpc.Reflection;

/// <summary>
/// An attribute that is used by our source generator to map an RPC interface to a
/// source generated proxy class.
/// </summary>
/// <param name="rpcInterface">The interface attributed with <see cref="RpcProxyAttribute"/>.</param>
/// <param name="proxyClass">
/// The source generated proxy class.
/// This must implement <paramref name="rpcInterface"/> and declare a public constructor
/// with a particular signature.
/// </param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public class RpcProxyMappingAttribute(
    Type rpcInterface,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type proxyClass) : Attribute
{
    /// <summary>
    /// Gets the RPC interface type that this mapping applies to.
    /// </summary>
    public Type RpcInterface => rpcInterface;

    /// <summary>
    /// Gets the proxy class type that this mapping applies to.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type ProxyClass => proxyClass;
}
