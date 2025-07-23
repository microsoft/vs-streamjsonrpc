// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record ParameterModel(string Name, string Type, string TypeNoNullRefAnnotations, RpcSpecialType SpecialType)
{
    internal static ParameterModel Create(IParameterSymbol parameter, KnownSymbols symbols)
        => new(parameter.Name, parameter.Type.ToDisplayString(ProxyGenerator.FullyQualifiedWithNullableFormat), parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), ProxyGenerator.ClassifySpecialType(parameter.Type, symbols));
}
