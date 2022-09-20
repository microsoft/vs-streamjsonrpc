// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Attribute to be used on an interface marked with <see cref="RpcMarshalableAttribute"/> to indicate that a known
/// inheriting interface exists.
/// </summary>
/// <remarks><para>
/// When an object that implements <see cref="SubType"/> is marshaled as its base interface marked with
/// <see cref="RpcMarshalableKnownSubTypeAttribute"/>, <see cref="SubTypeCode"/> is included in the StreamJsonRpc
/// message allowing the creation of a proxy which also implements <see cref="SubType"/>.
/// </para><para>
/// If a message is received containing no <see cref="SubTypeCode"/>, for a marshalable interface that has known
/// sub-types, a proxy will be created using the base interface. Unknown <see cref="SubTypeCode"/> values will be
/// ignored when creating the proxy.
/// </para><para>
/// <see cref="RpcMarshalableKnownSubTypeAttribute"/> is honored only when an object is marshaled through an RPC method
/// that used exactly the interface the attribute is assigned to: <see cref="RpcMarshalableKnownSubTypeAttribute"/>
/// attributes applied to interfaces extending or extended from the one the attribute is assigned to are ignored.
/// In other words, you cannot distribute this attribute across your type hierarchy tree and expect them all to be honored
/// at one location.
/// </para></remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
public class RpcMarshalableKnownSubTypeAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RpcMarshalableKnownSubTypeAttribute"/> class.
    /// </summary>
    /// <param name="subType">The <see cref="Type"/> of the known interface that extends the interface this attribute
    /// is applied to.</param>
    /// <param name="subTypeCode">The code to be serialized to specify that the marshaled object proxy should be
    /// generated implementing the <paramref name="subType"/> interface. <paramref name="subTypeCode"/> values must be
    /// unique across the different know sub-types of the interface this attribute is applied to. Because
    /// <paramref name="subTypeCode"/> is serialized when transmitting a marshaled object, its value should never be
    /// reassigned to a different sub-type when updating RPC interfaces.</param>
    public RpcMarshalableKnownSubTypeAttribute(Type subType, int subTypeCode)
    {
        this.SubType = subType;
        this.SubTypeCode = subTypeCode;
    }

    /// <summary>
    /// Gets the <see cref="Type"/> of the known interface that extends the interface this attribute is applied to.
    /// </summary>
    public Type SubType { get; }

    /// <summary>
    /// Gets the code to be serialized to specify that the marshaled object proxy should be generated also
    /// implementing the <see cref="SubType"/> interface.
    /// </summary>
    public int SubTypeCode { get; }
}
