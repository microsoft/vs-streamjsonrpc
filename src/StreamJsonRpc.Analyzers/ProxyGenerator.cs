// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using StreamJsonRpc.Analyzers;
using StreamJsonRpc.Analyzers.GeneratorModels;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// Source generator for StreamJsonRpc proxies.
/// </summary>
[Generator(LanguageNames.CSharp)]
public partial class ProxyGenerator : IIncrementalGenerator
{
    /// <summary>
    /// The namespace under which proxies (and interceptors) are generated.
    /// </summary>
    public const string GenerationNamespace = "StreamJsonRpc.Generated";

    internal static readonly SymbolDisplayFormat FullyQualifiedWithNullableFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    internal static readonly SymbolDisplayFormat FullyQualifiedNoGlobalWithNullableFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ImmutableEquatableArray<ProxyModel>> proxyProvider = context.SyntaxProvider.ForAttributeWithMetadataName<ImmutableEquatableArray<ProxyModel>>(
            Types.JsonRpcContractAttribute.FullName,
            (node, cancellationToken) => true,
            (context, cancellationToken) =>
            {
                if (context.TargetSymbol is not INamedTypeSymbol iface)
                {
                    return [];
                }

                if (!KnownSymbols.TryCreate(context.SemanticModel.Compilation, out KnownSymbols? symbols))
                {
                    return [];
                }

                // Skip inaccessible interfaces.
                if (!context.SemanticModel.Compilation.IsSymbolAccessibleWithin(context.TargetSymbol, context.SemanticModel.Compilation.Assembly))
                {
                    // Reported by StreamJsonRpc0001
                    return [];
                }

                IEnumerable<ProxyModel> proxies = ExpandInterfaceToGroups(iface, symbols)
                    .Select(group => new ProxyModel(
                        [.. group.Select(i => InterfaceModel.Create(i, symbols, declaredInThisCompilation: true, cancellationToken))]));

                return [.. proxies];
            });

        IncrementalValuesProvider<AttachUse> attachUseProvider = context.SyntaxProvider.CreateSyntaxProvider(
            (node, cancellationToken) => node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Attach" } },
            (context, cancellationToken) =>
            {
                var invocation = (InvocationExpressionSyntax)context.Node;

                if (!KnownSymbols.TryCreate(context.SemanticModel.Compilation, out KnownSymbols? symbols))
                {
                    return null;
                }

                (AttachSignature Signature, INamedTypeSymbol[] Interfaces, InterceptableLocation InterceptableLocation, string? ExternalProxyName)? analysis =
                    TryGetInterceptInfo(invocation, context.SemanticModel, symbols, cancellationToken);

                if (analysis is null)
                {
                    return null;
                }

                // PERF: we're creating a new InterfaceModel for every invocation of Attach,
                // even though we may have already created it. Is there any way to reduce duplication here?
                return new AttachUse(
                    analysis.Value.InterceptableLocation,
                    analysis.Value.Signature,
                    [.. analysis.Value.Interfaces.Select(c => InterfaceModel.Create(c, symbols, declaredInThisCompilation: SymbolEqualityComparer.Default.Equals(c.ContainingAssembly, context.SemanticModel.Compilation.Assembly), cancellationToken))],
                    analysis.Value.ExternalProxyName);
            }).Where(m => m is not null)!;

        IncrementalValueProvider<bool> publicProxy = context.CompilationProvider.Select((c, token) => this.AreProxiesPublic(c));

        IncrementalValueProvider<FullModel> fullModel = proxyProvider.Collect().Combine(attachUseProvider.Collect()).Combine(publicProxy).Select(
            (combined, attach) =>
            {
                ImmutableArray<ProxyModel> proxies = [.. combined.Left.Left.SelectMany(g => g)];
                ImmutableArray<AttachUse> attachUses = combined.Left.Right;
                bool publicProxies = combined.Right;
                return new FullModel(proxies.ToImmutableEquatableSet(), attachUses.ToImmutableEquatableArray(), publicProxies);
            });

        context.RegisterSourceOutput(fullModel, (context, model) => model.GenerateSource(context));
    }

    internal static (AttachSignature Signature, INamedTypeSymbol[] Interfaces)? AnalyzeAttachInvocation(InvocationExpressionSyntax invocation, SemanticModel semanticModel, KnownSymbols symbols, CancellationToken cancellationToken)
    {
        Debug.Assert(invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Attach" }, "This method should only be called after this basic check is performed.");

        // First filter to methods on the JsonRpc class.
        ExpressionSyntax receiverSyntax = ((MemberAccessExpressionSyntax)invocation.Expression).Expression;
        ITypeSymbol? receiverSymbol = semanticModel.GetTypeInfo(receiverSyntax, cancellationToken).Type;
        if (receiverSymbol is not INamedTypeSymbol { Name: "JsonRpc", ContainingNamespace.Name: "StreamJsonRpc" })
        {
            // Not a JsonRpc.Attach invocation.
            return null;
        }

        // Figure out exactly which method is being invoked.
        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol methodSymbol)
        {
            return null;
        }

        (AttachSignature Signature, INamedTypeSymbol[] Interfaces)? analysis = methodSymbol switch
        {
            { Parameters: [], TypeArguments: [INamedTypeSymbol iface] } => (AttachSignature.InstanceGeneric, new INamedTypeSymbol[] { iface }),
            { Parameters: [{ Type.Name: "JsonRpcProxyOptions" }], TypeArguments: [INamedTypeSymbol iface] } => (AttachSignature.InstanceGenericOptions, [iface]),
            { Parameters: [{ Type: INamedTypeSymbol { Name: "Type" } parameterType }], TypeArguments: [] } when TryGetNamedType(parameterType, out INamedTypeSymbol? argumentType) => (AttachSignature.InstanceNonGeneric, [argumentType]),
            { Parameters: [{ Type: INamedTypeSymbol { Name: "Type" } parameterType }, { Type.Name: "JsonRpcProxyOptions" }], TypeArguments: [] } when TryGetNamedType(parameterType, out INamedTypeSymbol? argumentType) => (AttachSignature.InstanceNonGenericOptions, [argumentType]),
            { Parameters: [{ Type: INamedTypeSymbol { Name: "ReadOnlySpan", TypeArguments: [{ Name: "Type" }] } parameterType }, { Type.Name: "JsonRpcProxyOptions" }], TypeArguments: [] } when TryGetNamedTypes(parameterType, out INamedTypeSymbol[]? argumentTypes) => (AttachSignature.InstanceNonGenericSpanOptions, argumentTypes),
            { Parameters: [{ Type: INamedTypeSymbol { Name: "Stream" } }], TypeArguments: [INamedTypeSymbol iface] } => (AttachSignature.StaticGenericStream, [iface]),
            { Parameters: [{ Type: INamedTypeSymbol { Name: "Stream" } }, { Type: INamedTypeSymbol { Name: "Stream" } }], TypeArguments: [INamedTypeSymbol iface] } => (AttachSignature.StaticGenericStreamStream, [iface]),
            { Parameters: [{ Type: INamedTypeSymbol { Name: "IJsonRpcMessageHandler" } }], TypeArguments: [INamedTypeSymbol iface] } => (AttachSignature.StaticGenericHandler, [iface]),
            { Parameters: [{ Type: INamedTypeSymbol { Name: "IJsonRpcMessageHandler" } }, { Type.Name: "JsonRpcProxyOptions" }], TypeArguments: [INamedTypeSymbol iface] } => (AttachSignature.StaticGenericHandlerOptions, [iface]),
            _ => null,
        };

        return analysis;

        bool TryGetNamedType(INamedTypeSymbol parameterType, [NotNullWhen(true)] out INamedTypeSymbol? namedTypeArgument)
        {
            if (!SymbolEqualityComparer.Default.Equals(parameterType, symbols.SystemType) ||
                invocation.ArgumentList.Arguments is not [ArgumentSyntax { Expression: TypeOfExpressionSyntax { Type: TypeSyntax argTypeSyntax } }, ..] ||
                semanticModel.GetTypeInfo(argTypeSyntax, cancellationToken) is not { Type: INamedTypeSymbol argTypeSymbol })
            {
                namedTypeArgument = null;
                return false;
            }

            namedTypeArgument = argTypeSymbol;
            return true;
        }

        bool TryGetNamedTypes(INamedTypeSymbol parameterType, [NotNullWhen(true)] out INamedTypeSymbol[]? namedTypeArgument)
        {
            if (invocation.ArgumentList.Arguments is [ArgumentSyntax { Expression: CollectionExpressionSyntax { Elements: { } elements } }, ..])
            {
                namedTypeArgument = new INamedTypeSymbol[elements.Count];
                for (int i = 0; i < elements.Count; i++)
                {
                    if (elements[i] is ExpressionElementSyntax { Expression: TypeOfExpressionSyntax { Type: { } namedTypeSyntax } } &&
                        semanticModel.GetTypeInfo(namedTypeSyntax, cancellationToken) is { Type: INamedTypeSymbol namedTypeSymbol })
                    {
                        namedTypeArgument[i] = namedTypeSymbol;
                    }
                    else
                    {
                        namedTypeArgument = null;
                        return false;
                    }
                }

                return true;
            }
            else if (invocation.ArgumentList.Arguments is [ArgumentSyntax { Expression: ArrayCreationExpressionSyntax { Initializer: { Expressions: { } expressions } } }, ..])
            {
                namedTypeArgument = new INamedTypeSymbol[expressions.Count];
                for (int i = 0; i < expressions.Count; i++)
                {
                    if (expressions[i] is TypeOfExpressionSyntax { Type: { } namedTypeSyntax } &&
                        semanticModel.GetTypeInfo(namedTypeSyntax, cancellationToken) is { Type: INamedTypeSymbol namedTypeSymbol })
                    {
                        namedTypeArgument[i] = namedTypeSymbol;
                    }
                    else
                    {
                        namedTypeArgument = null;
                        return false;
                    }
                }

                return true;
            }

            namedTypeArgument = null;
            return false;
        }
    }

    internal static (AttachSignature Signature, INamedTypeSymbol[] Interfaces, InterceptableLocation InterceptableLocation, string? ExternalProxyName)? TryGetInterceptInfo(InvocationExpressionSyntax invocation, SemanticModel semanticModel, KnownSymbols symbols, CancellationToken cancellationToken)
    {
        (AttachSignature Signature, INamedTypeSymbol[] Interfaces)? analysis = AnalyzeAttachInvocation(invocation, semanticModel, symbols, cancellationToken);
        if (analysis is null)
        {
            // We don't (yet) support intercepting this Attach method.
            return null;
        }

        // Only act on interfaces attributed with [JsonRpcContract] so we know they've been vetted.
        if (analysis.Value.Interfaces.Any(iface => !iface.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, symbols.JsonRpcContractAttribute))))
        {
            return null;
        }

        if (semanticModel.GetInterceptableLocation(invocation, cancellationToken) is not { } interceptableLocation)
        {
            return null;
        }

        // Look for an existing external proxy to reuse, if available.
        if (TryGetImplementingProxy(analysis.Value.Interfaces, symbols, out string? externalProxyName))
        {
            // Score! There's an existing proxy that suits our needs.
        }
        else if (analysis.Value.Interfaces.Any(iface => !IsAllowedToGenerateProxyFor(iface)))
        {
            // If we're not allowed to generate a proxy for any of the interfaces, then don't generate a proxy at all since it will be inadequate.
            return null;
        }

        return (analysis.Value.Signature, analysis.Value.Interfaces, interceptableLocation, externalProxyName);

        bool IsAllowedToGenerateProxyFor(INamedTypeSymbol iface)
        {
            if (!semanticModel.Compilation.IsSymbolAccessibleWithin(iface, semanticModel.Compilation.Assembly))
            {
                // We cannot access the interface at the assembly level, so don't try to implement it.
                return false;
            }

            if (SymbolEqualityComparer.Default.Equals(iface.ContainingAssembly, semanticModel.Compilation.Assembly))
            {
                // We're always allowed to generate a proxy for an interface declared in the same assembly.
                return true;
            }

            // This assembly-level attribute, set on the assembly containing the interface, *might* forbid source generating a proxy in an external assembly.
            AttributeData? attData = iface.ContainingAssembly.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, symbols.ExportRpcContractProxiesAttribute));
            if (attData is not null &&
                attData.NamedArguments.FirstOrDefault(d => d.Key == Types.ExportRpcContractProxiesAttribute.ForbidExternalProxyGeneration) is { Value.Value: true })
            {
                // We're forbidden from generating a proxy for this interface in an external assembly.
                return false;
            }

            return true;
        }
    }

    internal static RpcSpecialType ClassifySpecialType(ITypeSymbol type, KnownSymbols symbols)
    {
        return type as INamedTypeSymbol switch
        {
            { SpecialType: SpecialType.System_Void } => RpcSpecialType.Void,
            { IsGenericType: true } namedType when Equal(namedType.ConstructedFrom, symbols.TaskOfT) => RpcSpecialType.Task,
            { IsGenericType: true } namedType when Equal(namedType.ConstructedFrom, symbols.ValueTaskOfT) => RpcSpecialType.ValueTask,
            { IsGenericType: true } namedType when Equal(namedType.ConstructedFrom, symbols.IAsyncEnumerableOfT) => RpcSpecialType.IAsyncEnumerable,
            { IsGenericType: false } namedType when Equal(type, symbols.Task) => RpcSpecialType.Task,
            { IsGenericType: false } namedType when Equal(type, symbols.ValueTask) => RpcSpecialType.ValueTask,
            { IsGenericType: false } namedType when Equal(type, symbols.CancellationToken) => RpcSpecialType.CancellationToken,
            _ => RpcSpecialType.Other,
        };

        static bool Equal(ITypeSymbol candidate, ITypeSymbol? standard) => standard is not null && SymbolEqualityComparer.Default.Equals(candidate, standard);
    }

    private static bool TryGetImplementingProxy(INamedTypeSymbol[] ifaces, KnownSymbols symbols, out string? implementingProxyName)
    {
        HashSet<INamedTypeSymbol> requiredInterfaces = new(ifaces, SymbolEqualityComparer.Default);
        foreach (INamedTypeSymbol iface in requiredInterfaces)
        {
            foreach (AttributeData att in iface.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(att.AttributeClass, symbols.JsonRpcProxyMappingAttribute))
                {
                    continue;
                }

                if (att.ConstructorArguments is not [{ Value: INamedTypeSymbol proxyType }])
                {
                    continue;
                }

                // Test the proxy for the interfaces we need.
                if (requiredInterfaces.IsSubsetOf(proxyType.Interfaces))
                {
                    // TODO: skip proxies that implement too many interfaces.
                    implementingProxyName = proxyType.ToDisplayString(FullyQualifiedNoGlobalWithNullableFormat);
                    return true;
                }
            }
        }

        implementingProxyName = null;
        return false;
    }

    private static IEnumerable<INamedTypeSymbol[]> ExpandInterfaceToGroups(INamedTypeSymbol primary, KnownSymbols symbols)
    {
        bool anyGroupsDefined = false;
        foreach (AttributeData att in primary.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(att.AttributeClass, symbols.JsonRpcProxyInterfaceGroupAttribute))
            {
                continue;
            }

            if (att.ConstructorArguments is not [{ Kind: TypedConstantKind.Array, Values: ImmutableArray<TypedConstant> additionalInterfaces }])
            {
                continue;
            }

            anyGroupsDefined = true;
            yield return [primary, .. additionalInterfaces.Select(v => v.Value).OfType<INamedTypeSymbol>()];
        }

        if (!anyGroupsDefined)
        {
            yield return [primary]; // No groups defined, so just return the primary interface as its own group.
        }
    }

    private bool AreProxiesPublic(Compilation compilation)
        => compilation.Assembly.GetAttributes().Any(a => a is { AttributeClass: { Name: Types.ExportRpcContractProxiesAttribute.Name, ContainingNamespace: { Name: "StreamJsonRpc", ContainingNamespace.IsGlobalNamespace: true } } });
}
