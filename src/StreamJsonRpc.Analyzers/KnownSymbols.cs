// Copyright (c) Microsoft Corporation. All rights reserved.
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
    INamedTypeSymbol CancellationToken,
    INamedTypeSymbol IDisposable,
    INamedTypeSymbol RpcMarshalableAttribute,
    INamedTypeSymbol RpcMarshalableOptionalInterface,
    INamedTypeSymbol JsonRpcContractAttribute,
    INamedTypeSymbol JsonRpcProxyInterfaceGroupAttribute,
    INamedTypeSymbol ExportRpcContractProxiesAttribute,
    INamedTypeSymbol JsonRpcProxyMappingAttribute,
    INamedTypeSymbol JsonRpcMethodAttribute,
    INamedTypeSymbol SystemType,
    INamedTypeSymbol Stream)
{
    internal static bool TryCreate(Compilation compilation, [NotNullWhen(true)] out KnownSymbols? symbols)
    {
        INamedTypeSymbol? task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        INamedTypeSymbol? taskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        INamedTypeSymbol? valueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
        INamedTypeSymbol? valueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
        INamedTypeSymbol? asyncEnumerableOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1");
        INamedTypeSymbol? cancellationToken = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
        INamedTypeSymbol? idisposable = compilation.GetTypeByMetadataName("System.IDisposable");
        INamedTypeSymbol? rpcMarshalableAttribute = compilation.GetTypeByMetadataName(Types.RpcMarshalableAttribute.FullName);
        INamedTypeSymbol? rpcMarshalableOptionalInterface = compilation.GetTypeByMetadataName(Types.RpcMarshalableOptionalInterfaceAttribute.FullName);
        INamedTypeSymbol? rpcContractAttribute = compilation.GetTypeByMetadataName(Types.JsonRpcContractAttribute.FullName);
        INamedTypeSymbol? jsonRpcProxyInterfaceGroupAttribute = compilation.GetTypeByMetadataName(Types.JsonRpcProxyInterfaceGroupAttribute.FullName);
        INamedTypeSymbol? exportRpcContractProxiesAttribute = compilation.GetTypeByMetadataName(Types.ExportRpcContractProxiesAttribute.FullName);
        INamedTypeSymbol? rpcProxyMappingAttribute = compilation.GetTypeByMetadataName(Types.JsonRpcProxyMappingAttribute.FullName);
        INamedTypeSymbol? jsonRpcMethodAttribute = compilation.GetTypeByMetadataName(Types.JsonRpcMethodAttribute.FullName);
        INamedTypeSymbol? systemType = compilation.GetTypeByMetadataName("System.Type");
        INamedTypeSymbol? systemIOStream = compilation.GetTypeByMetadataName("System.IO.Stream");

        if (idisposable is null ||
            rpcMarshalableAttribute is null ||
            rpcMarshalableOptionalInterface is null ||
            rpcContractAttribute is null ||
            jsonRpcProxyInterfaceGroupAttribute is null ||
            exportRpcContractProxiesAttribute is null ||
            rpcProxyMappingAttribute is null ||
            jsonRpcMethodAttribute is null ||
            systemType is null ||
            systemIOStream is null ||
            cancellationToken is null)
        {
            symbols = null;
            return false;
        }

        symbols = new KnownSymbols(task, taskOfT, valueTask, valueTaskOfT, asyncEnumerableOfT, cancellationToken, idisposable, rpcMarshalableAttribute, rpcMarshalableOptionalInterface, rpcContractAttribute, jsonRpcProxyInterfaceGroupAttribute, exportRpcContractProxiesAttribute, rpcProxyMappingAttribute, jsonRpcMethodAttribute, systemType, systemIOStream);
        return true;
    }
}
