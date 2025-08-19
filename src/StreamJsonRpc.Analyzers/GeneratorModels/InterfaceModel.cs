// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using StreamJsonRpc.Analyzers;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

/// <summary>
/// Describes an interface for which a proxy may be generated.
/// </summary>
/// <param name="FullName">The full name of the type, including generic type parameters.</param>
/// <param name="Name">The leaf name of the type, excluding generic type parameters.</param>
/// <param name="TypeParameters">A list of generic type parameters on the interface.</param>
/// <param name="Container">The declaring type or namespace in which the interface is declared.</param>
/// <param name="Methods">The methods in the interface.</param>
/// <param name="Events">The events in the interface.</param>
/// <param name="HasUnsupportedMemberTypes">Indicates whether the interface has additional members that are not supported.</param>
internal record InterfaceModel(string FullName, string Name, ImmutableEquatableArray<string> TypeParameters, Container? Container, ImmutableEquatableArray<MethodModel> Methods, ImmutableEquatableArray<EventModel> Events, bool HasUnsupportedMemberTypes)
{
    internal required bool IsPartial { get; init; }

    internal required bool IsPublic { get; init; }

    internal bool IsFullyPartial => this.IsPartial && this.Container is null or { IsFullyPartial: true };

    internal required bool DeclaredInThisCompilation { get; init; }

    internal required ImmutableEquatableSet<string> PrescribedTypeArgs { get; init; }

    internal static InterfaceModel Create(INamedTypeSymbol iface, KnownSymbols symbols, bool declaredInThisCompilation, CancellationToken cancellationToken)
    {
        bool hasUnsupportedMemberTypes = false;
        List<MethodModel> methods = [];
        List<EventModel> events = [];

        foreach (ISymbol member in iface.GetAllMembers())
        {
            switch (member)
            {
                case { IsStatic: true }:
                    // Ignore all static members.
                    break;
                case IMethodSymbol method when SymbolEqualityComparer.Default.Equals(method.ContainingType, symbols.IDisposable):
                    // We don't map this special Dispose method.
                    break;
                case IMethodSymbol { AssociatedSymbol: not null }:
                    // We'll handle these as part of the associated symbol.
                    break;
                case IMethodSymbol method:
                    methods.Add(MethodModel.Create(method, symbols));
                    break;
                case IEventSymbol evt when EventModel.Create(evt, symbols) is EventModel evtModel:
                    events.Add(evtModel);
                    break;
                case INamedTypeSymbol nestedType:
                    // We ignore these.
                    break;
                default:
                    hasUnsupportedMemberTypes = true;
                    break;
            }
        }

        HashSet<string> prescribedTypeArgs = [];
        foreach (AttributeData attr in iface.GetAttributes())
        {
            if (attr is { AttributeClass: { TypeArguments: [INamedTypeSymbol { TypeArguments: { Length: > 0 } typeArgs }] } attrClass }
                && SymbolEqualityComparer.Default.Equals(attrClass.ConstructUnboundGenericType(), symbols.JsonRpcProxyAttribute))
            {
                prescribedTypeArgs.Add(string.Join(", ", typeArgs.Select(ta => ta.ToDisplayString(ProxyGenerator.FullyQualifiedNoGlobalWithNullableFormat))));
            }
        }

        return new InterfaceModel(
            iface.ToDisplayString(ProxyGenerator.FullyQualifiedNoGlobalWithNullableFormat),
            iface.Name,
            [.. iface.TypeParameters.Select(tp => tp.Name)],
            Container.CreateFor((INamespaceOrTypeSymbol?)iface.ContainingType ?? iface.ContainingNamespace, cancellationToken),
            methods.ToImmutableEquatableArray(),
            events.ToImmutableEquatableArray(),
            hasUnsupportedMemberTypes)
        {
            IsPartial = iface.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is InterfaceDeclarationSyntax syntax && syntax.Modifiers.Any(SyntaxKind.PartialKeyword),
            IsPublic = iface.IsActuallyPublic(),
            DeclaredInThisCompilation = declaredInThisCompilation,
            PrescribedTypeArgs = prescribedTypeArgs.ToImmutableEquatableSet(),
        };
    }
}
