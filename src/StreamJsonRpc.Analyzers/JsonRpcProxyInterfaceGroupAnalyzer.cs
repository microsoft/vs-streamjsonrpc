// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// Analyzer for uses of the <c>JsonRpcProxyInterfaceGroupAttribute</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class JsonRpcProxyInterfaceGroupAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Diagnostic ID for "All interfaces in a proxy group must be attributed".
    /// </summary>
    public const string TypesAttributedAsContractsDiagnosticId = "StreamJsonRpc0006";

    /// <summary>
    /// Diagnostic for StreamJsonRpc0006: All interfaces in a proxy group must be attributed.
    /// </summary>
    public static readonly DiagnosticDescriptor TypesAttributedAsContractsDiagnostic = new(
        id: TypesAttributedAsContractsDiagnosticId,
        title: Strings.StreamJsonRpc0006_Title,
        messageFormat: Strings.StreamJsonRpc0006_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(TypesAttributedAsContractsDiagnosticId));

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
        TypesAttributedAsContractsDiagnostic,
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
        List<INamedTypeSymbol> group = [];

        IEnumerable<AttributeData>? groupAttributes =
            namedType.GetAttributes().Where(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, knownSymbols.JsonRpcProxyInterfaceGroupAttribute));

        foreach (AttributeData groupAttribute in groupAttributes)
        {
            group.Clear();
            group.Add(namedType);

            if (groupAttribute.ConstructorArguments is [{ } addlInterfaces])
            {
                foreach (TypedConstant arg in addlInterfaces.Values)
                {
                    if (arg.Kind == TypedConstantKind.Type && arg.Value is INamedTypeSymbol interfaceType)
                    {
                        group.Add(interfaceType);
                    }
                }
            }

            // Check that all interfaces in the group are attributed as contracts.
            List<INamedTypeSymbol> errors = [];
            foreach (INamedTypeSymbol interfaceType in group)
            {
                if (!interfaceType.GetAttributes().Any(attr => SymbolEqualityComparer.Default.Equals(attr.AttributeClass, knownSymbols.JsonRpcContractAttribute)))
                {
                    errors.Add(interfaceType);
                }
            }

            if (errors.Count > 0)
            {
                Location? location = groupAttribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? namedType.Locations.FirstOrDefault();
                context.ReportDiagnostic(Diagnostic.Create(
                    TypesAttributedAsContractsDiagnostic,
                    location,
                    string.Join(", ", errors.Select(g => g.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)))));
            }
        }
    }
}
