// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Linq;
using Microsoft.CodeAnalysis;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record ParameterModel(string Name, string RpcName, string Type, string TypeNoNullRefAnnotations, RpcSpecialType SpecialType)
{
    internal static ParameterModel Create(IParameterSymbol parameter, KnownSymbols symbols)
    {
        AttributeData? jsonRpcParameterAttribute = parameter.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, symbols.JsonRpcParameterAttribute));
        string rpcName = jsonRpcParameterAttribute is { ConstructorArguments: [{ Value: string name }, ..] } ? name : parameter.Name;

        return new(
            parameter.Name,
            rpcName,
            parameter.Type.ToDisplayString(ProxyGenerator.FullyQualifiedWithNullableFormat),
            parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ProxyGenerator.ClassifySpecialType(parameter.Type, symbols));
    }
}
