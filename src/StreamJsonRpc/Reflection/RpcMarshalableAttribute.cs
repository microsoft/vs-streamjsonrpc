// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Designates an interface that is used in an RPC contract to marshal the object so the receiver can invoke remote methods on it instead of serializing the object to send its data to the remote end.
/// </summary>
/// <remarks>
/// <see href="https://github.com/microsoft/vs-streamjsonrpc/blob/main/doc/rpc_marshalable_objects.md">Learn more about marshalable interfaces</see>.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public class RpcMarshalableAttribute : Attribute
{
    /// <summary>
    /// Gets a value indicating whether the marshaled object is only allowed in requests
    /// and may only be invoked by the receiver until the response is sent.
    /// </summary>
    /// <remarks>
    /// <para>Objects marshaled via an interface attributed with this property set to true may only be used as RPC method parameters.
    /// They will not be allowed as return values from RPC methods.</para>
    /// <para>While the receiver may dispose of the proxy they receive, this disposal will <em>not</em> propagate to the sender,
    /// and their originating object will <em>not</em> be disposed of.
    /// The original object owner retains ownership of the lifetime of the object after the RPC call.</para>
    /// </remarks>
    public bool CallScopedLifetime { get; init; }
}
