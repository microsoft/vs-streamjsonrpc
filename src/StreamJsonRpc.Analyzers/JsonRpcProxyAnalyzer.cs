// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// Analyzes use of the <c>JsonRpcProxyAttribute</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class JsonRpcProxyAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Diagnostic ID for "JsonRpcProxyAttribute&lt;T&gt; should be applied only to generic interfaces".
    /// </summary>
    public const string GenericInterfaceRequiredId = "StreamJsonRpc0030";

    /// <summary>
    /// Diagnostic ID for "JsonRpcProxyAttribute&lt;T&gt; type argument should be a closed instance of the applied type".
    /// </summary>
    public const string TypeArgIsClosedInterfaceId = "StreamJsonRpc0031";

    /// <summary>
    /// Diagnostic ID for "JsonRpcProxyAttribute&lt;T&gt; should be accompanied by another contract attribute".
    /// </summary>
    public const string ContractAttributeRequiredId = "StreamJsonRpc0032";

    /// <summary>
    /// Diagnostic for StreamJsonRpc0030: JsonRpcProxyAttribute&lt;T&gt; should be applied only to generic interfaces.
    /// </summary>
    public static readonly DiagnosticDescriptor GenericInterfaceRequired = new(
        id: GenericInterfaceRequiredId,
        title: Strings.StreamJsonRpc0030_Title,
        messageFormat: Strings.StreamJsonRpc0030_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(GenericInterfaceRequiredId));

    /// <summary>
    /// Diagnostic for StreamJsonRpc0031: JsonRpcProxyAttribute&lt;T&gt; type argument should be a closed instance of the applied type.
    /// </summary>
    public static readonly DiagnosticDescriptor TypeArgIsClosedInterface = new(
        id: TypeArgIsClosedInterfaceId,
        title: Strings.StreamJsonRpc0031_Title,
        messageFormat: Strings.StreamJsonRpc0031_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(TypeArgIsClosedInterfaceId));

    /// <summary>
    /// Diagnostic for StreamJsonRpc0032: JsonRpcProxyAttribute&lt;T&gt; should be accompanied by another contract attribute.
    /// </summary>
    public static readonly DiagnosticDescriptor ContractAttributeRequired = new(
        id: ContractAttributeRequiredId,
        title: Strings.StreamJsonRpc0032_Title,
        messageFormat: Strings.StreamJsonRpc0032_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(ContractAttributeRequiredId));

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
        GenericInterfaceRequired,
        TypeArgIsClosedInterface,
        ContractAttributeRequired,
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
        AttributeData[] attrs = [.. namedType.GetAttributes().Where(a => a is { AttributeClass: { TypeArguments: [INamedTypeSymbol] } attrClass } && SymbolEqualityComparer.Default.Equals(attrClass.ConstructUnboundGenericType(), knownSymbols.JsonRpcProxyAttribute))];
        if (attrs is [])
        {
            return;
        }

        if (!namedType.IsGenericType)
        {
            Location bestLocation = attrs.FirstOrDefault(a => a.ApplicationSyntaxReference is not null)?.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? namedType.Locations.FirstOrDefault() ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(
                GenericInterfaceRequired,
                bestLocation,
                namedType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
        }

        if (!namedType.GetAttributes().Any(a =>
            SymbolEqualityComparer.Default.Equals(a.AttributeClass, knownSymbols.JsonRpcContractAttribute)
            || SymbolEqualityComparer.Default.Equals(a.AttributeClass, knownSymbols.RpcMarshalableAttribute)))
        {
            Location bestLocation = attrs.FirstOrDefault(a => a.ApplicationSyntaxReference is not null)?.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? namedType.Locations.FirstOrDefault() ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(
                ContractAttributeRequired,
                bestLocation,
                namedType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
        }

        if (!namedType.IsGenericType)
        {
            // The rest of the diagnostics depend on the applied type being generic.
            return;
        }

        INamedTypeSymbol unboundNamedType = namedType.ConstructUnboundGenericType();
        foreach (AttributeData attr in attrs)
        {
            if (attr.AttributeClass!.TypeArguments[0] is not INamedTypeSymbol { IsGenericType: true } ||
                !SymbolEqualityComparer.Default.Equals(unboundNamedType, ((INamedTypeSymbol)attr.AttributeClass!.TypeArguments[0]).ConstructUnboundGenericType()))
            {
                Location bestLocation = attr.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? namedType.Locations.FirstOrDefault() ?? Location.None;
                context.ReportDiagnostic(Diagnostic.Create(
                    TypeArgIsClosedInterface,
                    bestLocation,
                    namedType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    attr.AttributeClass!.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
            }
        }
    }
}
