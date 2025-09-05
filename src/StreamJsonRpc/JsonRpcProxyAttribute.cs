// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;

namespace StreamJsonRpc;

/// <summary>
/// Applied to an open generic interface that is also annotated with <see cref="JsonRpcContractAttribute"/> or <see cref="RpcMarshalableAttribute"/>
/// to ensure that a proxy of a particular closed generic type is available at runtime.
/// </summary>
/// <typeparam name="T">
/// The closed generic instance of the applied interface. For example if this attribute is applied to <c>IMyRpc&lt;T&gt;</c> then this type argument might be <c>IMyRpc&lt;int&gt;</c>.
/// Note it should be the entire closed interface type, not just the type argument.
/// </typeparam>
/// <remarks>
/// <para>
/// Applying this attribute requires at least C# language version 11.0.
/// Projects target lesser runtimes (e.g., .NET Framework) should set the <c>LangVersion</c> property to 11.0 or higher.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = true)]
[Conditional("NEVER")] // This attribute is only used at compile time to generate code, and .NET Framework fails on generic attributes, so don't let it survive compilation.
public class JsonRpcProxyAttribute<T> : Attribute
{
}
