// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using StreamJsonRpc.Analyzers;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record InterfaceModel(string Prefix, string FullName, string Name, Container? Container, ImmutableEquatableArray<MethodModel> Methods, ImmutableEquatableArray<EventModel> Events)
{
    internal required bool IsPartial { get; init; }

    internal bool IsFullyPartial => this.IsPartial && this.Container is null or { IsFullyPartial: true };

    internal required bool DeclaredInThisCompilation { get; init; }

    internal static InterfaceModel Create(INamedTypeSymbol iface, KnownSymbols symbols, bool declaredInThisCompilation, CancellationToken cancellationToken)
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
            iface.Name,
            Container.CreateFor((INamespaceOrTypeSymbol?)iface.ContainingType ?? iface.ContainingNamespace, cancellationToken),
            methods,
            events)
        {
            IsPartial = iface.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is InterfaceDeclarationSyntax syntax && syntax.Modifiers.Any(SyntaxKind.PartialKeyword),
            DeclaredInThisCompilation = declaredInThisCompilation,
        };
    }
}
