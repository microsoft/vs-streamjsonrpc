// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using RpcSpecialType = StreamJsonRpc.Analyzers.GeneratorModels.RpcSpecialType;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// An analyzer of StreamJsonRpc proxy contracts.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class JsonRpcContractAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0001: Inaccessible interface.
    /// </summary>
    public const string InaccessibleInterfaceId = "StreamJsonRpc0001";

    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0002: Declare partial interface.
    /// </summary>
    public const string PartialInterfaceId = "StreamJsonRpc0002";

    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0005: RpcMarshalable interfaces must be IDisposable.
    /// </summary>
    public const string RpcMarshableDisposableId = "StreamJsonRpc0005";

    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0007: Use RpcMarshalableAttribute on optional marshalable interface.
    /// </summary>
    public const string UseRpcMarshalableAttributeOnOptionalInterfacesId = "StreamJsonRpc0007";

    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0008: Add methods to PolyType shape for RPC contract interface.
    /// </summary>
    public const string GeneratePolyTypeMethodsOnRpcContractInterfaceId = "StreamJsonRpc0008";

    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0011: RPC methods use supported return types.
    /// </summary>
    public const string UnsupportedReturnTypeId = "StreamJsonRpc0011";

    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0012: RPC contracts may not include this type of member.
    /// </summary>
    public const string UnsupportedMemberTypeId = "StreamJsonRpc0012";

    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0013: RPC contracts may not include generic methods.
    /// </summary>
    public const string NoGenericMethodsId = "StreamJsonRpc0013";

    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0014: CancellationToken may only appear as the last parameter.
    /// </summary>
    public const string CancellationTokenPositionId = "StreamJsonRpc0014";

    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0015: RPC contracts may not be generic.
    /// </summary>
    public const string NoGenericInterfaceId = "StreamJsonRpc0015";

    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0016: RPC contracts may declare events only with <see cref="EventHandler"/> or <see cref="EventHandler{T}" /> delegate types.
    /// </summary>
    public const string UnsupportedEventDelegateId = "StreamJsonRpc0016";

    /// <summary>
    /// Diagnostic for StreamJsonRpc0001: Inaccessible interface.
    /// </summary>
    public static readonly DiagnosticDescriptor InaccessibleInterface = new(
        id: InaccessibleInterfaceId,
        title: Strings.StreamJsonRpc0001_Title,
        messageFormat: Strings.StreamJsonRpc0001_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(InaccessibleInterfaceId));

    /// <summary>
    /// Diagnostic for StreamJsonRpc0002: Declare partial interface.
    /// </summary>
    public static readonly DiagnosticDescriptor PartialInterface = new(
        id: PartialInterfaceId,
        title: Strings.StreamJsonRpc0002_Title,
        messageFormat: Strings.StreamJsonRpc0002_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(PartialInterfaceId));

    /// <summary>
    /// Diagnostic for StreamJsonRpc0005: RpcMarshalable interfaces must be IDisposable.
    /// </summary>
    public static readonly DiagnosticDescriptor RpcMarshableDisposable = new(
        id: RpcMarshableDisposableId,
        title: Strings.StreamJsonRpc0005_Title,
        messageFormat: Strings.StreamJsonRpc0005_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(RpcMarshableDisposableId));

    /// <summary>
    /// Diagnostic for StreamJsonRpc0007: Use RpcMarshalableAttribute on optional marshalable interface.
    /// </summary>
    public static readonly DiagnosticDescriptor UseRpcMarshalableAttributeOnOptionalInterfaces = new(
        id: UseRpcMarshalableAttributeOnOptionalInterfacesId,
        title: Strings.StreamJsonRpc0007_Title,
        messageFormat: Strings.StreamJsonRpc0007_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(UseRpcMarshalableAttributeOnOptionalInterfacesId));

    /// <summary>
    /// Diagnostic for StreamJsonRpc0008: Add methods to PolyType shape for RPC contract interface.
    /// </summary>
    public static readonly DiagnosticDescriptor GeneratePolyTypeMethodsOnRpcContractInterface = new(
        id: GeneratePolyTypeMethodsOnRpcContractInterfaceId,
        title: Strings.StreamJsonRpc0008_Title,
        messageFormat: Strings.StreamJsonRpc0008_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(GeneratePolyTypeMethodsOnRpcContractInterfaceId));

    /// <summary>
    /// Diagnostic for StreamJsonRpc0011: RPC methods use supported return types.
    /// </summary>
    public static readonly DiagnosticDescriptor UnsupportedReturnType = new(
        id: UnsupportedReturnTypeId,
        title: Strings.StreamJsonRpc0011_Title,
        messageFormat: Strings.StreamJsonRpc0011_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(UnsupportedReturnTypeId));

    /// <summary>
    /// Diagnostic for StreamJsonRpc0012: RPC contracts may not include this type of member.
    /// </summary>
    public static readonly DiagnosticDescriptor UnsupportedMemberType = new(
        id: UnsupportedMemberTypeId,
        title: Strings.StreamJsonRpc0012_Title,
        messageFormat: Strings.StreamJsonRpc0012_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(UnsupportedMemberTypeId));

    /// <summary>
    /// Diagnostic for StreamJsonRpc0013: RPC contracts may not include generic methods.
    /// </summary>
    public static readonly DiagnosticDescriptor NoGenericMethods = new(
        id: NoGenericMethodsId,
        title: Strings.StreamJsonRpc0013_Title,
        messageFormat: Strings.StreamJsonRpc0013_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(NoGenericMethodsId));

    /// <summary>
    /// Diagnostic for StreamJsonRpc0014: CancellationToken may only appear as the last parameter.
    /// </summary>
    public static readonly DiagnosticDescriptor CancellationTokenPosition = new(
        id: CancellationTokenPositionId,
        title: Strings.StreamJsonRpc0014_Title,
        messageFormat: Strings.StreamJsonRpc0014_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(CancellationTokenPositionId));

    /// <summary>
    /// Diagnostic for StreamJsonRpc0015: No generic interfaces.
    /// </summary>
    public static readonly DiagnosticDescriptor NoGenericInterface = new(
        id: NoGenericInterfaceId,
        title: Strings.StreamJsonRpc0015_Title,
        messageFormat: Strings.StreamJsonRpc0015_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(NoGenericInterfaceId));

    /// <summary>
    /// Diagnostic for StreamJsonRpc0016: Unsupported event delegate type.
    /// </summary>
    public static readonly DiagnosticDescriptor UnsupportedEventDelegate = new(
        id: UnsupportedEventDelegateId,
        title: Strings.StreamJsonRpc0016_Title,
        messageFormat: Strings.StreamJsonRpc0016_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(UnsupportedEventDelegateId));

    private static readonly SymbolDisplayFormat NameOnly = new();

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
        InaccessibleInterface,
        PartialInterface,
        RpcMarshableDisposable,
        UseRpcMarshalableAttributeOnOptionalInterfaces,
        GeneratePolyTypeMethodsOnRpcContractInterface,
        UnsupportedReturnType,
        UnsupportedMemberType,
        NoGenericMethods,
        CancellationTokenPosition,
        NoGenericInterface,
        UnsupportedEventDelegate,
    ];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        if (!Debugger.IsAttached)
        {
            context.EnableConcurrentExecution();
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.ReportDiagnostics);

        context.RegisterCompilationStartAction(
            context =>
            {
                if (!KnownSymbols.TryCreate(context.Compilation, out KnownSymbols? knownSymbols))
                {
                    return;
                }

                context.RegisterSymbolStartAction(
                    context => this.InspectSymbol(context, knownSymbols, (INamedTypeSymbol)context.Symbol),
                    SymbolKind.NamedType);
            });
    }

    private static void ReportMemberDiagnostic(SymbolStartAnalysisContext context, INamedTypeSymbol namedType, ISymbol member, Location? locationAroundMember, Func<Location?, IEnumerable<Location>?, SymbolDisplayFormat, Diagnostic> createDiagnostic)
    {
        if (SymbolEqualityComparer.Default.Equals(member.ContainingType, namedType))
        {
            // We can just use the location around member.
            context.RegisterSymbolEndAction(context => context.ReportDiagnostic(createDiagnostic(locationAroundMember, null, NameOnly)));
        }
        else
        {
            Location? bestLocation = null;
            context.RegisterSyntaxNodeAction(
                context =>
                {
                    var syntax = (SimpleBaseTypeSyntax)context.Node;
                    ITypeSymbol? baseType = context.SemanticModel.GetTypeInfo(syntax.Type, context.CancellationToken).Type;
                    if (baseType is null)
                    {
                        return;
                    }

                    if (SymbolEqualityComparer.Default.Equals(member.ContainingType, baseType) ||
                        (bestLocation is null && member.ContainingType.IsAssignableFrom(baseType)))
                    {
                        bestLocation = syntax.GetLocation();
                    }
                },
                SyntaxKind.SimpleBaseType);
            context.RegisterSymbolEndAction(context => context.ReportDiagnostic(createDiagnostic(bestLocation, locationAroundMember is null ? null : [locationAroundMember], SymbolDisplayFormat.CSharpErrorMessageFormat)));
        }
    }

    private bool IncludesPublicMethods(AttributeData? generateShapeAttribute)
    {
        const int PublicInstanceMethods = 1; // MethodShapeFlags.PublicInstance
        return generateShapeAttribute?.NamedArguments.FirstOrDefault(na => na.Key == "IncludeMethods") is { Value: { Kind: TypedConstantKind.Enum, Value: int v } }
            && (v & PublicInstanceMethods) == PublicInstanceMethods;
    }

    private void InspectSymbol(SymbolStartAnalysisContext context, KnownSymbols knownSymbols, INamedTypeSymbol namedType)
    {
        AttributeData? rpcContractAttribute =
            namedType.GetAttributes().FirstOrDefault(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, knownSymbols.JsonRpcContractAttribute)) ??
            namedType.GetAttributes().FirstOrDefault(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, knownSymbols.RpcMarshalableAttribute));
        if (rpcContractAttribute is null)
        {
            return;
        }

        bool isRpcMarshalable = SymbolEqualityComparer.Default.Equals(rpcContractAttribute.AttributeClass, knownSymbols.RpcMarshalableAttribute);
        bool isCallScopedLifetime = rpcContractAttribute.NamedArguments.FirstOrDefault(a => a.Key == Types.RpcMarshalableAttribute.CallScopedLifetime).Value.Value is true;
        ImmutableList<Diagnostic> diagnostics = [];
        Location typeLocation = namedType.Locations.FirstOrDefault() ?? Location.None;
        bool hasGenericTypeParameters = namedType.TypeArguments.Any(ta => ta is ITypeParameterSymbol);

        // All RPC contracts should have shapes generated for them that include methods.
        // GenerateShapeAttribute is ineffective on open generic types, so ignore it in that case.
        AttributeData? generateShapeAttribute = hasGenericTypeParameters ? null : namedType.GetAttributes().FirstOrDefault(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, knownSymbols.GenerateShapeAttribute));
        AttributeData? typeShapeAttribute = namedType.GetAttributes().FirstOrDefault(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, knownSymbols.TypeShapeAttribute));
        if (!this.IncludesPublicMethods(typeShapeAttribute) && !this.IncludesPublicMethods(generateShapeAttribute))
        {
            bool preferGenerateShape = generateShapeAttribute is not null || !isRpcMarshalable;
            diagnostics = diagnostics.Add(Diagnostic.Create(
                GeneratePolyTypeMethodsOnRpcContractInterface,
                typeLocation,
                [(generateShapeAttribute ?? typeShapeAttribute)?.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken)?.GetLocation() ?? Location.None],
                ImmutableDictionary<string, string?>.Empty.Add("PreferGenerateShape", preferGenerateShape ? "true" : "false"),
                namedType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                preferGenerateShape ? "[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]" : "[TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]"));
        }

        AttributeData[] optionalIfaceAttrs = [.. namedType.GetAttributes().Where(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, knownSymbols.RpcMarshalableOptionalInterface))];

        BaseTypeDeclarationSyntax? syntax = namedType.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) as BaseTypeDeclarationSyntax;

        if (!context.Compilation.IsSymbolAccessibleWithin(namedType, context.Compilation.Assembly))
        {
            if (syntax is { Identifier: { } id })
            {
                diagnostics = diagnostics.Add(Diagnostic.Create(InaccessibleInterface, id.GetLocation(), namedType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
            }
        }

        if (!hasGenericTypeParameters && this.GetNonPartialElements(namedType, context.CancellationToken) is { Count: > 0 } nonPartialElements)
        {
            Location[] additionalLocations = nonPartialElements.Select(e => e.Location).ToArray();
            string nonPartialElementsList = string.Join(", ", nonPartialElements.Select(e => e.Symbol.ToDisplayString(GenerationHelpers.QualifiedNameOnlyFormat)));
            diagnostics = diagnostics.Add(Diagnostic.Create(PartialInterface, additionalLocations[0], additionalLocations.Skip(1), namedType.ToDisplayString(GenerationHelpers.QualifiedNameOnlyFormat), nonPartialElementsList));
        }

        if (namedType.IsGenericType && !isRpcMarshalable)
        {
            diagnostics = diagnostics.Add(Diagnostic.Create(NoGenericInterface, typeLocation));
        }

        if (isRpcMarshalable && !isCallScopedLifetime && !knownSymbols.IDisposable.IsAssignableFrom(namedType))
        {
            diagnostics = diagnostics.Add(Diagnostic.Create(RpcMarshableDisposable, typeLocation, namedType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
        }

        foreach (AttributeData optionalIfaceAttr in optionalIfaceAttrs)
        {
            if (optionalIfaceAttr.ConstructorArguments is [_, { Kind: TypedConstantKind.Type, Value: INamedTypeSymbol ifaceType }])
            {
                // TODO: check for IsOptional on the attribute
                AttributeData? marshalableAttribute = ifaceType.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, knownSymbols.RpcMarshalableAttribute));
                if (marshalableAttribute is null || marshalableAttribute.NamedArguments.FirstOrDefault(na => na.Key is Types.RpcMarshalableAttribute.IsOptional).Value is not { Value: true })
                {
                    diagnostics = diagnostics.Add(Diagnostic.Create(
                        UseRpcMarshalableAttributeOnOptionalInterfaces,
                        optionalIfaceAttr.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation(),
                        ifaceType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
                }
            }
        }

        foreach (ISymbol member in namedType.GetAllMembers())
        {
            // Members may be declared within namedType, or in a base type.
            // We need to have the primary location be within namedType so roslyn knows when to clear it,
            // but additional locations can point within the base type if we have syntax for it.
            Location? location = member.Locations.FirstOrDefault();

            switch (member)
            {
                case { IsStatic: true }:
                    // Ignore all static members, as they are not part of the RPC contract.
                    break;
                case IMethodSymbol { IsGenericMethod: true } method:
                    ReportMemberDiagnostic(context, namedType, method, location, (loc, addl, memberFormat) => Diagnostic.Create(NoGenericMethods, loc, addl, namedType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), member.ToDisplayString(memberFormat)));
                    break;
                case IMethodSymbol { MethodKind: MethodKind.PropertySet or MethodKind.PropertyGet or MethodKind.EventAdd or MethodKind.EventRemove }:
                    // Suppress diagnostic because the overall property or event will be reported.
                    break;
                case IMethodSymbol method:
                    this.InspectMethod(context, knownSymbols, method, namedType);
                    break;
                case IEventSymbol evt when !isRpcMarshalable:
                    // Events must be declared with either the EventHandler or EventHandler<T> delegate type.
                    if (evt is not { Type: { Name: "EventHandler", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } })
                    {
                        ReportMemberDiagnostic(context, namedType, member, location, (loc, addl, _) => Diagnostic.Create(UnsupportedEventDelegate, loc, addl));
                    }

                    break;
                case ITypeSymbol:
                    // We don't care about nested types, so skip them.
                    break;
                default:
                    ReportMemberDiagnostic(context, namedType, member, location, (loc, addl, memberFormat) => Diagnostic.Create(UnsupportedMemberType, loc, addl, namedType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), member.ToDisplayString(memberFormat)));
                    break;
            }
        }

        if (diagnostics.Count > 0)
        {
            context.RegisterSymbolEndAction(context =>
            {
                foreach (Diagnostic diagnostic in diagnostics)
                {
                    context.ReportDiagnostic(diagnostic);
                }
            });
        }
    }

    private IReadOnlyList<(ITypeSymbol Symbol, Location Location)> GetNonPartialElements(INamedTypeSymbol namedType, CancellationToken cancellationToken)
    {
        List<(ITypeSymbol, Location)>? discovered = null;

        ITypeSymbol? target = namedType;
        do
        {
            BaseTypeDeclarationSyntax? syntax = target.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as BaseTypeDeclarationSyntax;
            if (syntax?.Modifiers.Any(SyntaxKind.PartialKeyword) is not true)
            {
                (discovered ??= []).Add((target, syntax?.Identifier.GetLocation() ?? Location.None));
            }

            target = target.ContainingType;
        }
        while (target is not null);

        return discovered ?? [];
    }

    private void InspectMethod(SymbolStartAnalysisContext context, KnownSymbols knownSymbols, IMethodSymbol method, INamedTypeSymbol namedType)
    {
        if (!this.IsAllowedMethodReturnType(method.ReturnType, knownSymbols))
        {
            MethodDeclarationSyntax? methodDeclaration = (MethodDeclarationSyntax?)method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken);
            Location diagnosticLocation = methodDeclaration?.ReturnType.GetLocation() ?? method.Locations.FirstOrDefault() ?? Location.None;

            ReportMemberDiagnostic(context, namedType, method, diagnosticLocation, (location, addl, memberFormat) => Diagnostic.Create(
                UnsupportedReturnType,
                location,
                addl,
                method.ReturnType.Name,
                method.ToDisplayString(memberFormat)));
        }

        // Verify that no parameter up to the second-to-last is a CancellationToken.
        for (int i = 0; i <= method.Parameters.Length - 2; i++)
        {
            IParameterSymbol parameter = method.Parameters[i];
            if (parameter.Type.Equals(knownSymbols.CancellationToken, SymbolEqualityComparer.Default))
            {
                Location diagnosticLocation = parameter.Locations.FirstOrDefault() ?? Location.None;
                ReportMemberDiagnostic(context, namedType, method, diagnosticLocation, (location, addl, memberFormat) => Diagnostic.Create(
                    CancellationTokenPosition,
                    location,
                    addl,
                    parameter.Type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    method.ToDisplayString(memberFormat)));
            }
        }
    }

    private bool IsAllowedMethodReturnType(ITypeSymbol returnType, KnownSymbols knownSymbols)
        => ProxyGenerator.ClassifySpecialType(returnType, knownSymbols) switch
        {
            RpcSpecialType.Void or RpcSpecialType.Task or RpcSpecialType.ValueTask or RpcSpecialType.IAsyncEnumerable => true,
            _ => false,
        };
}
