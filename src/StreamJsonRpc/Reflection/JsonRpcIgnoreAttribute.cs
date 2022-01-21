// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Attribute which identifies methods that should <em>not</em> be invokable over RPC.
/// </summary>
/// <remarks>
/// <para>When adding an RPC target object via <see cref="JsonRpc.AddLocalRpcTarget(object)"/> or other APIs,
/// all public methods on the object default to being exposed to invocation by the client.
/// When <see cref="JsonRpcTargetOptions.AllowNonPublicInvocation"/> is set, even more methods are exposed.
/// Applying this attribute to any method will ensure it can never be invoked directly by the RPC client.</para>
/// <para>If <see cref="JsonRpcMethodAttribute"/> and <see cref="JsonRpcIgnoreAttribute"/> are applied to the same method,
/// an exception will be thrown at the time of adding the object as an RPC target.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public class JsonRpcIgnoreAttribute : Attribute
{
}
