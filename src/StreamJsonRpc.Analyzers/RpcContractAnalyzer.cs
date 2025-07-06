// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using SpecialType = StreamJsonRpc.Analyzers.ProxyGenerator.SpecialType;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// An analyzer of StreamJsonRpc proxy contracts.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class RpcContractAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0001: RPC methods use supported return types.
    /// </summary>
    public const string UnsupportedReturnTypeId = "StreamJsonRpc0001";

    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0002: Inaccessible interface.
    /// </summary>
    public const string InaccessibleInterfaceId = "StreamJsonRpc0002";

    /// <summary>
    /// Diagnostic for StreamJsonRpc0001: RPC methods use supported return types.
    /// </summary>
    public static readonly DiagnosticDescriptor UnsupportedReturnType = new(
        id: UnsupportedReturnTypeId,
        title: Strings.StreamJsonRpc0001_Title,
        messageFormat: Strings.StreamJsonRpc0001_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(UnsupportedReturnTypeId));

    /// <summary>
    /// Diagnostic for StreamJsonRpc0002: Inacessible interface.
    /// </summary>
    public static readonly DiagnosticDescriptor InaccessibleInterface = new(
        id: InaccessibleInterfaceId,
        title: Strings.StreamJsonRpc0002_Title,
        messageFormat: Strings.StreamJsonRpc0002_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(InaccessibleInterfaceId));

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
        UnsupportedReturnType,
        InaccessibleInterface,
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
                    context =>
                    {
                        var namedType = (INamedTypeSymbol)context.Symbol;
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

                        foreach (ISymbol member in namedType.GetMembers())
                        {
                            switch (member)
                            {
                                case IMethodSymbol method:
                                    if (!this.IsAllowedMethodReturnType(method.ReturnType, knownSymbols))
                                    {
                                        MethodDeclarationSyntax? methodDeclaration = (MethodDeclarationSyntax?)method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken);
                                        Location diagnosticLocation = methodDeclaration?.ReturnType.GetLocation() ?? method.Locations.FirstOrDefault() ?? Location.None;

                                        context.ReportDiagnostic(Diagnostic.Create(
                                            UnsupportedReturnType,
                                            diagnosticLocation,
                                            method.ReturnType.Name));
                                    }

                                    break;
                            }
                        }
                    },
                    SymbolKind.NamedType);
            });
    }

    private bool IsAllowedMethodReturnType(ITypeSymbol returnType, KnownSymbols knownSymbols)
        => ProxyGenerator.ClassifySpecialType(returnType, knownSymbols) switch
        {
            SpecialType.Void or SpecialType.Task or SpecialType.ValueTask or SpecialType.IAsyncEnumerable => true,
            _ => false,
        };
}
