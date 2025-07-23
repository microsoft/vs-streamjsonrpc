// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// Analyzes uses of the <c>Attach</c> method to encourage correct usage of the <c>JsonRpcContractAttribute</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class AttachAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// The ID for <see cref="MissingRpcContractAttribute"/>.
    /// </summary>
    public const string MissingRpcContractAttributeId = "StreamJsonRpc0003";

    /// <summary>
    /// The ID for <see cref="OnlyInterfaceTypesAllowed"/>.
    /// </summary>
    public const string OnlyInterfaceTypesAllowedId = "StreamJsonRpc0004";

    /// <summary>
    /// The diagnostic descriptor for reporting when Attach is called with an interface lacking the <c>JsonRpcContractAttribute</c>.
    /// </summary>
    public static readonly DiagnosticDescriptor MissingRpcContractAttribute = new DiagnosticDescriptor(
        id: MissingRpcContractAttributeId,
        title: Strings.StreamJsonRpc0003_Title,
        messageFormat: Strings.StreamJsonRpc0003_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(MissingRpcContractAttributeId));

    /// <summary>
    /// The diagnostic descriptor for reporting when Attach is called with a class instead of an interface.
    /// </summary>
    public static readonly DiagnosticDescriptor OnlyInterfaceTypesAllowed = new DiagnosticDescriptor(
        id: OnlyInterfaceTypesAllowedId,
        title: Strings.StreamJsonRpc0004_Title,
        messageFormat: Strings.StreamJsonRpc0004_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(OnlyInterfaceTypesAllowedId));

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
        MissingRpcContractAttribute,
        OnlyInterfaceTypesAllowed,
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

                context.RegisterOperationAction(
                    context =>
                    {
                        try
                        {
                            IInvocationOperation invocationOp = (IInvocationOperation)context.Operation;
                            if (invocationOp.SemanticModel is null ||
                                invocationOp.Syntax is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Attach" } } invocationSyntax)
                            {
                                return;
                            }

                            (GeneratorModels.AttachSignature Signature, INamedTypeSymbol[]? Interfaces)? analysis = ProxyGenerator.AnalyzeAttachInvocation(invocationSyntax, invocationOp.SemanticModel, knownSymbols, context.CancellationToken);
                            if (analysis is not { Interfaces: not null })
                            {
                                return;
                            }

                            // Go through each interface. If we have the syntax for it (as source code), look for the attribute and report a diagnostic if it's missing.
                            foreach (INamedTypeSymbol iface in analysis.Value.Interfaces)
                            {
                                if (iface is not { TypeKind: TypeKind.Interface, IsUnboundGenericType: false })
                                {
                                    // We only allow interfaces to be attached.
                                    context.ReportDiagnostic(Diagnostic.Create(OnlyInterfaceTypesAllowed, invocationOp.Syntax.GetLocation(), iface.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
                                    continue;
                                }

                                if (iface.Locations.FirstOrDefault()?.IsInSource is not true)
                                {
                                    continue;
                                }

                                AttributeData? attData = iface.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, knownSymbols.JsonRpcContractAttribute));
                                if (attData is null)
                                {
                                    context.ReportDiagnostic(Diagnostic.Create(MissingRpcContractAttribute, invocationOp.Syntax.GetLocation(), iface.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
                                }
                            }
                        }
                        catch (Exception) when (AnalyzerUtilities.LaunchDebugger())
                        {
                            throw;
                        }
                    },
                    OperationKind.Invocation);
            });
    }
}
