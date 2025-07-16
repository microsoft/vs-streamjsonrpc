// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace StreamJsonRpc.Reflection;

/// <summary>
/// An attribute that is used by our source generator to map an RPC interface to a
/// source generated proxy class.
/// </summary>
/// <param name="proxyClass">
/// The source generated proxy class.
/// This must implement the interface the attribute is applied to and declare a public constructor
/// with a particular signature.
/// </param>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public class JsonRpcProxyMappingAttribute(
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type proxyClass) : Attribute
{
    /// <summary>
    /// Gets the proxy class type implements the RPC interface.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type ProxyClass => proxyClass;
}
