﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace StreamJsonRpc.Analyzers;

internal record KnownSymbols(
    INamedTypeSymbol? Task,
    INamedTypeSymbol? TaskOfT,
    INamedTypeSymbol? ValueTask,
    INamedTypeSymbol? ValueTaskOfT,
    INamedTypeSymbol? IAsyncEnumerableOfT,
    INamedTypeSymbol? CancellationToken)
{
    internal static bool TryCreate(Compilation compilation, [NotNullWhen(true)] out KnownSymbols? symbols)
    {
        INamedTypeSymbol? task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        INamedTypeSymbol? taskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        INamedTypeSymbol? valueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
        INamedTypeSymbol? valueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
        INamedTypeSymbol? asyncEnumerableOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1");
        INamedTypeSymbol? cancellationToken = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");

        symbols = new KnownSymbols(task, taskOfT, valueTask, valueTaskOfT, asyncEnumerableOfT, cancellationToken);
        return true;
    }
}
