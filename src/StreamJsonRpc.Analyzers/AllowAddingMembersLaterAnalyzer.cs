// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// Analyzes usages of the AllowAddingMembersLaterAttribute.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class AllowAddingMembersLaterAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0100: Use RpcContractAttribute with AllowAddingMembersLaterAttribute.
    /// </summary>
    public const string PairWithRpcContractId = "StreamJsonRpc0100";

    /// <summary>
    /// Diagnostic for StreamJsonRpc0100: Use RpcContractAttribute with AllowAddingMembersLaterAttribute.
    /// </summary>
    public static readonly DiagnosticDescriptor PairWithRpcContract = new(
        id: PairWithRpcContractId,
        title: Strings.StreamJsonRpc0100_Title,
        messageFormat: Strings.StreamJsonRpc0100_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(PairWithRpcContractId));

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
        PairWithRpcContract,
    ];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.ReportDiagnostics);
        if (!Debugger.IsAttached)
        {
            context.EnableConcurrentExecution();
        }

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
        AttributeData? allowAddingMembersLaterAttribute = namedType.GetAttributes()
            .FirstOrDefault(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, knownSymbols.AllowAddingMembersLaterAttribute));
        AttributeData? rpcContractAttribute = namedType.GetAttributes()
            .FirstOrDefault(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, knownSymbols.RpcContractAttribute));

        if (allowAddingMembersLaterAttribute is not null && rpcContractAttribute is null)
        {
            SyntaxNode? attributeSyntax = allowAddingMembersLaterAttribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken);
            Location location = attributeSyntax?.GetLocation() ?? namedType.Locations.FirstOrDefault() ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(PairWithRpcContract, location));
        }
    }
}
