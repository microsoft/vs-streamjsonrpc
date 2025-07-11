// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;
using StreamJsonRpc.Analyzers;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record InterfaceModel(string Prefix, string InterfaceName, ImmutableEquatableArray<MethodModel> Methods, ImmutableEquatableArray<EventModel> Events)
{
    internal static InterfaceModel Create(INamedTypeSymbol iface, KnownSymbols symbols)
    {
        ImmutableEquatableArray<MethodModel> methods = new([..
            iface.GetAllMembers()
                .OfType<IMethodSymbol>()
                .Where(m => m.AssociatedSymbol is null && !SymbolEqualityComparer.Default.Equals(m.ContainingType, symbols.IDisposable))
                .Select(method => MethodModel.Create(method, symbols))]);

        ImmutableEquatableArray<EventModel> events = new([..
            iface.GetAllMembers()
                .OfType<IEventSymbol>()
                .Select(evt => EventModel.Create(evt, symbols))
                .Where(evt => evt is not null)!]);

        string fileNamePrefix = iface.ToDisplayString(GenerationHelpers.QualifiedNameOnlyFormat);
        return new InterfaceModel(
            fileNamePrefix,
            iface.ToDisplayString(ProxyGenerator.FullyQualifiedNoGlobalWithNullableFormat),
            methods,
            events);
    }
}
