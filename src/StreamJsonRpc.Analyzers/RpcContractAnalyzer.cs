// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using RpcSpecialType = StreamJsonRpc.Analyzers.GeneratorModels.RpcSpecialType;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// An analyzer of StreamJsonRpc proxy contracts.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class RpcContractAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0010: Inaccessible interface.
    /// </summary>
    public const string InaccessibleInterfaceId = "StreamJsonRpc0010";

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
    /// Diagnostic for StreamJsonRpc0010: Inaccessible interface.
    /// </summary>
    public static readonly DiagnosticDescriptor InaccessibleInterface = new(
        id: InaccessibleInterfaceId,
        title: Strings.StreamJsonRpc0010_Title,
        messageFormat: Strings.StreamJsonRpc0010_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(InaccessibleInterfaceId));

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

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
        InaccessibleInterface,
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

                context.RegisterSymbolAction(
                    context => this.InspectSymbol(context, knownSymbols, (INamedTypeSymbol)context.Symbol),
                    SymbolKind.NamedType);
            });
    }

    private void InspectSymbol(SymbolAnalysisContext context, KnownSymbols knownSymbols, INamedTypeSymbol namedType)
    {
        AttributeData? rpcContractAttribute = namedType.GetAttributes()
            .FirstOrDefault(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, knownSymbols.RpcContractAttribute));
        if (rpcContractAttribute is null)
        {
            return;
        }

        if (!context.Compilation.IsSymbolAccessibleWithin(namedType, context.Compilation.Assembly))
        {
            if (namedType.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is BaseTypeDeclarationSyntax { Identifier: { } id })
            {
                context.ReportDiagnostic(Diagnostic.Create(InaccessibleInterface, id.GetLocation()));
            }
        }

        if (namedType.IsGenericType)
        {
            context.ReportDiagnostic(Diagnostic.Create(NoGenericInterface, namedType.Locations.FirstOrDefault() ?? Location.None));
        }

        foreach (ISymbol member in namedType.GetMembers())
        {
            switch (member)
            {
                case IMethodSymbol { IsGenericMethod: true } method:
                    context.ReportDiagnostic(Diagnostic.Create(NoGenericMethods, method.Locations.FirstOrDefault() ?? Location.None));
                    break;
                case IMethodSymbol { MethodKind: MethodKind.PropertySet or MethodKind.PropertyGet }:
                    // Suppress diagnostic because the overall property will be reported.
                    break;
                case IMethodSymbol method:
                    this.InspectMethod(context, knownSymbols, method);
                    break;
                case IEventSymbol evt:
                    // Events must be declared with either the EventHandler or EventHandler<T> delegate type.
                    if (evt is not { Type: { Name: "EventHandler", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } })
                    {
                        context.ReportDiagnostic(Diagnostic.Create(UnsupportedEventDelegate, evt.Locations.FirstOrDefault() ?? Location.None));
                    }

                    break;
                case { Locations: [Location location, ..] }:
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedMemberType, location));
                    break;
            }
        }
    }

    private void InspectMethod(SymbolAnalysisContext context, KnownSymbols knownSymbols, IMethodSymbol method)
    {
        if (!this.IsAllowedMethodReturnType(method.ReturnType, knownSymbols))
        {
            MethodDeclarationSyntax? methodDeclaration = (MethodDeclarationSyntax?)method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken);
            Location diagnosticLocation = methodDeclaration?.ReturnType.GetLocation() ?? method.Locations.FirstOrDefault() ?? Location.None;

            context.ReportDiagnostic(Diagnostic.Create(
                UnsupportedReturnType,
                diagnosticLocation,
                method.ReturnType.Name));
        }

        // Verify that no parameter up to the second-to-last is a CancellationToken.
        for (int i = 0; i <= method.Parameters.Length - 2; i++)
        {
            IParameterSymbol parameter = method.Parameters[i];
            if (parameter.Type.Equals(knownSymbols.CancellationToken, SymbolEqualityComparer.Default))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    CancellationTokenPosition,
                    parameter.Locations.FirstOrDefault() ?? Location.None));
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
