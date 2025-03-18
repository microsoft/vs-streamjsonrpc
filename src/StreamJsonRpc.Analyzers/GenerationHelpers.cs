// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// Helper methods and properties for a source generator.
/// </summary>
internal static class GenerationHelpers
{
    /// <summary>
    /// Gets a string providing a fully qualified name, without the global:: prefix.
    /// </summary>
    internal static SymbolDisplayFormat QualifiedNameOnlyFormat { get; } =
        new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

    internal static IEnumerable<TResult> Select<T, TResult>(this ReadOnlyMemory<T> memory, Func<T, TResult> selector)
    {
        for (int i = 0; i < memory.Length; i++)
        {
            yield return selector(memory.Span[i]);
        }
    }

    internal static IEnumerable<ISymbol> GetAllMembers(this ITypeSymbol symbol)
    {
        if (symbol.BaseType is not null)
        {
            foreach (ISymbol baseMember in symbol.BaseType.GetAllMembers())
            {
                yield return baseMember;
            }
        }

        foreach (INamedTypeSymbol iface in symbol.AllInterfaces)
        {
            foreach (ISymbol member in iface.GetMembers())
            {
                yield return member;
            }
        }

        foreach (ISymbol member in symbol.GetMembers())
        {
            yield return member;
        }
    }
}
