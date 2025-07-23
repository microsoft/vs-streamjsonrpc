// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1629 // Documentation should end with a period.

using System.Diagnostics;

namespace StreamJsonRpc.Reflection;

/// <summary>
/// Contains inputs required for proxy generation.
/// </summary>
/// <remarks>
/// This struct serves as a forward compatible way to initialize source generated proxies from source generated code.
/// Members may be added to it over time such that previously source generated proxies not only continue to work
/// but propagate the new values to <see cref="ProxyBase"/> automatically.
/// </remarks>
[DebuggerDisplay($"{{{nameof(Requirements)},nq}}")]
public readonly struct ProxyInputs
{
    /// <summary>
    /// Gets the interface that describes the functions available on the remote end.
    /// </summary>
    public required Type ContractInterface { get; init; }

    /// <summary>
    /// Gets <inheritdoc cref="ProxyGeneration.Get" path="/param[@name='additionalContractInterfaces']"/>
    /// </summary>
    public ReadOnlyMemory<Type> AdditionalContractInterfaces { get; init; }

    /// <summary>
    /// Gets the set of customizations for how the client proxy is wired up. If <see langword="null" />, default options will be used.
    /// </summary>
    public JsonRpcProxyOptions? Options { get; init; }

    /// <summary>
    /// Gets <inheritdoc cref="ProxyGeneration.Get" path="/param[@name='implementedOptionalInterfaces']"/>
    /// </summary>
    internal ReadOnlyMemory<(Type Type, int Code)> ImplementedOptionalInterfaces { get; init; }

    /// <summary>
    /// Gets the handle to the remote object that is being marshaled via this proxy.
    /// </summary>
    internal long? MarshaledObjectHandle { get; init; }

    /// <summary>
    /// Gets a description of the requirements on the proxy to be used.
    /// </summary>
    internal string Requirements => $"Implementing interface(s): {string.Join(", ", [this.ContractInterface, .. this.AdditionalContractInterfaces.Span])}.";
}
