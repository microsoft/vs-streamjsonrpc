// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Emit source generated proxies for RPC interfaces with <see langword="public"/> visibility
/// when the interface annotated with <see cref="RpcContractAttribute"/> is itself <see langword="public"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public class ExportRpcContractProxiesAttribute : Attribute
{
    /// <summary>
    /// Gets or sets a value indicating whether to forbid the source generation of proxies
    /// for the RPC interfaces declared in this assembly by other assemblies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By default, source generated proxies may be generated for interfaces that are annotated with
    /// <see cref="RpcContractAttribute"/> in the assembly that declares the interface
    /// or other assemblies that reference the declaring assembly.
    /// Implementations of an RPC interface in another assembly will break when members
    /// are added to the interface.
    /// To preserve the ability to add members to an RPC interface in the future without recompiling
    /// all referencing assemblies, set this property to <see langword="true" />, which forces the
    /// calling assembly to use the source generated proxies within this assembly or to use
    /// dynamically generated proxies at runtime.
    /// </para>
    /// <para>
    /// Changing or removing existing members is always a binary-breaking change because referencing assemblies
    /// may have code that invokes these members.
    /// </para>
    /// </remarks>
    public bool ForbidExternalProxyGeneration { get; set; }
}
