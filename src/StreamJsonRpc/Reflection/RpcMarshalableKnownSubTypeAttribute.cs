// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Attribute to be used on an interface marked with <see cref="RpcMarshalableAttribute"/> to indicate that a known
/// inherited interface exists.
///
/// When an object that implements <see cref="SubType"/> is marshaled as its base interface marked with
/// <see cref="RpcMarshalableKnownSubTypeAttribute"/>, <see cref="SubTypeCode"/> is included in the StreamJsonRpc
/// message allowing the creation of a proxy which implements <see cref="SubType"/>.
/// </summary>
/// <remarks>
/// If an object implements more than one of the <see cref="SubType"/> insterfaces, the behavior is undefined.
///
/// In an object doesn't implement any of the <see cref="SubType"/> interfaces,
/// <see cref="RpcMarshalableKnownSubTypeAttribute"/> attributes are simply ignored.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
public class RpcMarshalableKnownSubTypeAttribute : Attribute
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="subType"></param>
    /// <param name="subTypeCode"></param>
    public RpcMarshalableKnownSubTypeAttribute(Type subType, int subTypeCode)
    {
        this.SubType = subType;
        this.SubTypeCode = subTypeCode;
    }

    /// <summary>
    /// 
    /// </summary>
    public Type SubType { get; }

    /// <summary>
    /// Gets the code to be serialized to specify that the marshaled object proxy should be generated implementing the
    /// <see cref="SubType"/> interface.
    /// </summary>
    public int SubTypeCode { get; }
}
