// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace StreamJsonRpc;

/// <summary>
/// Designates an interface that is used in an RPC contract to marshal the object so the receiver can invoke remote methods on it instead of serializing the object to send its data to the remote end.
/// </summary>
/// <remarks>
/// <see href="https://github.com/microsoft/vs-streamjsonrpc/blob/main/doc/rpc_marshalable_objects.md">Learn more about marshable interfaces</see>.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public class RpcMarshalableAttribute : Attribute
{
}
