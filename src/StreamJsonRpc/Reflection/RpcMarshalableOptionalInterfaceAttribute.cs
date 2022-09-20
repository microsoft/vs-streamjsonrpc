// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Attribute to be used on an interface marked with <see cref="RpcMarshalableAttribute"/> to indicate that the
/// marshalable object may also implement the interface <see cref="OptionalInterface"/>.
/// </summary>
/// <remarks><para>
/// When an object that implements <see cref="OptionalInterface"/> is marshaled as its base interface marked with
/// <see cref="RpcMarshalableOptionalInterfaceAttribute"/>, <see cref="OptionalInterfaceCode"/> is included in the
/// StreamJsonRpc message allowing the creation of a proxy which also implements <see cref="OptionalInterface"/>.
/// </para><para>
/// If a message is received containing no <see cref="OptionalInterfaceCode"/> values, for a marshalable interface that
/// has known optional interfaces, a proxy will be created using the base interface. Unknown
/// <see cref="OptionalInterfaceCode"/> values will be ignored when creating the proxy.
/// </para><para>
/// <see cref="RpcMarshalableOptionalInterfaceAttribute"/> is honored only when an object is marshaled through an RPC
/// method that used exactly the interface the attribute is assigned to:
/// <see cref="RpcMarshalableOptionalInterfaceAttribute"/> attributes applied to interfaces extending or extended from
/// the one the attribute is assigned to are ignored.
/// </para></remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
public class RpcMarshalableOptionalInterfaceAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RpcMarshalableOptionalInterfaceAttribute"/> class.
    /// </summary>
    /// <param name="optionalInterface">The <see cref="Type"/> of the known optional interface that the marshalable
    /// object may implement.</param>
    /// <param name="optionalInterfaceCode">The code to be serialized to specify that the marshaled object proxy should
    /// be generated implementing the <paramref name="optionalInterface"/> interface.
    /// <paramref name="optionalInterfaceCode"/> values must be unique across the different optional interfaces of the
    /// type this attribute is applied to. Because <paramref name="optionalInterfaceCode"/> is serialized when
    /// transmitting a marshaled object, its value should never be reassigned to a different optional interface when
    /// updating RPC interfaces.</param>
    public RpcMarshalableOptionalInterfaceAttribute(Type optionalInterface, int optionalInterfaceCode)
    {
        this.OptionalInterface = optionalInterface;
        this.OptionalInterfaceCode = optionalInterfaceCode;
    }

    /// <summary>
    /// Gets the <see cref="Type"/> of the known optional interface that the marshalable object may implement.
    /// </summary>
    public Type OptionalInterface { get; }

    /// <summary>
    /// Gets the code to be serialized to specify that the marshaled object proxy should be generated implementing the
    /// <see cref="OptionalInterface"/> interface.
    /// </summary>
    public int OptionalInterfaceCode { get; }
}
