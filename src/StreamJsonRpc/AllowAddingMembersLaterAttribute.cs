// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc;

/// <summary>
/// Sets policies for one or more <see cref="RpcContractAttribute"/> interfaces
/// to preserve the option to add members to the interface in the future
/// without causing a binary-breaking change.
/// </summary>
/// <remarks>
/// <para>
/// By default, source generated proxies may be generated for interfaces that are annotated with
/// <see cref="RpcContractAttribute"/> in the assembly that declares the interface
/// or other assemblies that reference the declaring assembly.
/// Implementations of an RPC interface in another assembly will break when members
/// are added to the interface.
/// To preserve the ability to add members to an RPC interface in the future without recompiling
/// all referencing assemblies, apply this attribute to the interface or assembly.
/// </para>
/// <para>
/// Applying this attribute allows source generated proxies to be generated within the same assembly,
/// but not in referencing assemblies.
/// Referencing assemblies may use dynamically generated proxies or the source generated proxies
/// offered by the assembly that declares the interface, but may not source generate their own proxies.
/// </para>
/// <para>
/// Changing or removing existing members is still a binary-breaking change because referencing assemblies
/// may have code that invokes these members.
/// </para>
/// <para>
/// When this attribute is applied to an interface, the policy is set to that interface only.
/// This interface should also have the <see cref="RpcContractAttribute"/> applied.
/// </para>
/// <para>
/// When this attribute is applied to an assembly, the policy applies to all interfaces
/// in that assembly that are annotated with <see cref="RpcContractAttribute"/>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Assembly)]
public class AllowAddingMembersLaterAttribute : Attribute;
