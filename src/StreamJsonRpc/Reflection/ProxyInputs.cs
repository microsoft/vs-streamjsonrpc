// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1629 // Documentation should end with a period.

using System.Diagnostics;
using System.Text;
using PolyType.Abstractions;

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
    internal ProxyInputs(ProxyInputs copyFrom)
    {
        this.ContractInterface = copyFrom.ContractInterface;
        this.AdditionalContractInterfaces = copyFrom.AdditionalContractInterfaces;
        this.Options = copyFrom.Options;
        this.ImplementedOptionalInterfaces = copyFrom.ImplementedOptionalInterfaces;
        this.MarshaledObjectHandle = copyFrom.MarshaledObjectHandle;
        this.ContractInterfaceShape = copyFrom.ContractInterfaceShape;
    }

    /// <summary>
    /// Gets the primary/main interface that describes the functions available on the remote end.
    /// </summary>
    public required Type ContractInterface { get; init; }

    /// <summary>
    /// Gets a list of additional interfaces that the client proxy should implement <em>without</em> the name transformation or event limitations
    /// involved with <see cref="ImplementedOptionalInterfaces"/>.
    /// This set should have an empty intersection with <see cref="ImplementedOptionalInterfaces"/>.
    /// </summary>
    public ReadOnlyMemory<Type> AdditionalContractInterfaces { get; init; }

    /// <summary>
    /// Gets the set of customizations for how the client proxy is wired up. If <see langword="null" />, default options will be used.
    /// </summary>
    public JsonRpcProxyOptions? Options { get; init; }

    /// <summary>
    /// Gets a list of additional marshalable interfaces that the client proxy should implement.
    /// Methods on these interfaces are invoked using a special name transformation that includes an integer code,
    /// ensuring that methods do not suffer from name collisions across interfaces.
    /// </summary>
    internal ReadOnlyMemory<(Type Type, int Code)> ImplementedOptionalInterfaces { get; init; }

    /// <summary>
    /// Gets the handle to the remote object that is being marshaled via this proxy.
    /// </summary>
    internal long? MarshaledObjectHandle { get; init; }

    /// <summary>
    /// Gets the shape of the contract interface, if available.
    /// </summary>
    internal ITypeShape? ContractInterfaceShape { get; init; }

    /// <summary>
    /// Gets a description of the requirements on the proxy to be used.
    /// </summary>
    internal string Requirements => $"Implementing interface(s): {string.Join(", ", [this.ContractInterface, .. this.AdditionalContractInterfaces.Span])}.";

    /// <summary>
    /// Sorts <paramref name="list"/> so that:
    /// <list type="number">
    /// <item><description>interfaces that are extending a lesser number of other interfaces in <paramref name="list"/> come first;</description></item>
    /// <item><description>interfaces extending the same number of other interfaces in <paramref name="list"/>, are ordered by optional interface code;
    /// where a <see langword="null" /> code comes first.</description></item>
    /// </list>
    /// </summary>
    /// <param name="list">The list of RPC interfaces to be sorted.</param>
    internal static void SortRpcInterfaces(IList<(Type Type, int? Code)> list)
    {
        (Type Type, int? Code, int InheritanceWeight)[] weightedList
            = [.. list.Select(i => (i.Type, i.Code, list.Count(i2 => i2.Type.IsAssignableFrom(i.Type))))];
        Array.Sort(weightedList, CompareRpcInterfaces);

        for (int i = 0; i < weightedList.Length; i++)
        {
            list[i] = (weightedList[i].Type, weightedList[i].Code);
        }

        int CompareRpcInterfaces((Type Type, int? Code, int InheritanceWeight) a, (Type Type, int? Code, int InheritanceWeight) b)
        {
            int weightComparison = a.InheritanceWeight.CompareTo(b.InheritanceWeight);
            return (weightComparison, a.Code, b.Code) switch
            {
                (_, _, _) when weightComparison != 0 => weightComparison,
                (_, null, null) => 0,
                (_, null, _) => -1,
                (_, _, null) => 1,
                (_, _, _) => a.Code.Value.CompareTo(b.Code.Value),
            };
        }
    }

    internal Exception CreateNoSourceGeneratedProxyException()
    {
        StringBuilder builder = new();
        builder.Append(this.ContractInterface.FullName ?? this.ContractInterface.Name);
        foreach (Type additionalInterface in this.AdditionalContractInterfaces.Span)
        {
            builder.Append(", ");
            builder.Append(additionalInterface.FullName ?? additionalInterface.Name);
        }

        return new NotImplementedException(Resources.FormatNoSourceGeneratedProxyAvailable(builder));
    }
}
