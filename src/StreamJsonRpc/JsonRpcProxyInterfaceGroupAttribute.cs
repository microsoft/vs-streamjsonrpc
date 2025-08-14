// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Specifies the set of additional interfaces that a source generated proxy should implement.
/// </summary>
/// <param name="additionalInterfaces">
/// The interfaces to include in the set with the applied interface in a source generated proxy.
/// Each of these interfaces must also be annotated with <see cref="JsonRpcContractAttribute"/>.
/// </param>
/// <remarks>
/// <para>
/// The interface the attribute is applied to is implicitly included in the group.
/// </para>
/// <para>
/// Each occurrence of this attribute produces a distinct proxy class.
/// One proxy with all possible interfaces is sufficient if the client allows <see cref="JsonRpcProxyOptions.AcceptProxyWithExtraInterfaces"/>.
/// Otherwise, an interface group should be defined for each set of interfaces that a client may ask for.
/// </para>
/// <para>
/// For a proxy that implements multiple interfaces, this attribute may appear on any one of them and identify the others.
/// It is recommended to apply it to the primary interface that is most commonly used by clients.
/// </para>
/// <para>
/// This attribute is meaningless unless accompanied by <see cref="JsonRpcContractAttribute"/>.
/// </para>
/// <para>
/// When this attribute is present, proxies will only be generated for the group(s) specified.
/// The applied interface itself may not get its own proxy generated unless an attribute with an empty set of additional interfaces is also applied to it.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = true)]
public class JsonRpcProxyInterfaceGroupAttribute(params Type[] additionalInterfaces) : Attribute
{
    /// <summary>
    /// Gets the additional interfaces that a source generated proxy should implement.
    /// </summary>
    public ReadOnlyMemory<Type> AdditionalInterfaces => additionalInterfaces;
}
