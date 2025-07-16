// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace StreamJsonRpc.Analyzers;

internal static class AnalyzerUtilities
{
    internal static string GetHelpLink(string diagnosticId) => $"https://microsoft.github.io/vs-streamjsonrpc/analyzers/{diagnosticId}.html";

    internal static bool IsAssignableFrom([NotNullWhen(true)] this ITypeSymbol? baseType, [NotNullWhen(true)] ITypeSymbol? type)
    {
        if (baseType is null || type is null)
        {
            return false;
        }

        SymbolEqualityComparer comparer = SymbolEqualityComparer.Default;

        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (comparer.Equals(current, baseType))
            {
                return true;
            }
        }

        foreach (INamedTypeSymbol @interface in type.AllInterfaces)
        {
            if (comparer.Equals(@interface, baseType))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool LaunchDebugger()
    {
#if DEBUG
        System.Diagnostics.Debugger.Launch();
#endif
        return false;
    }
}
