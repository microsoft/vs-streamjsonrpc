// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// Suppresses diagnostics related to <c>JsonRpc.Attach</c> invocations that our source generator
/// is able to intercept and replace with AOT safe code.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class AttachAOTDiagnosticSuppressor : DiagnosticSuppressor
{
    private const string Justification = "The target method has been intercepted by a generated static variant.";

    private const string RUCDiagnosticId = "IL2026";

    private const string RDCDiagnosticId = "IL3050";

    /// <summary>
    /// Suppression descriptor for IL2026: Members attributed with RequiresUnreferencedCode may break when trimming.
    /// </summary>
    private static readonly SuppressionDescriptor RUCDiagnostic = new(id: "SYSLIBSUPPRESS0002", suppressedDiagnosticId: RUCDiagnosticId, Justification);

    /// <summary>
    /// Suppression descriptor for IL3050: Avoid calling members annotated with 'RequiresDynamicCodeAttribute' when publishing as native AOT.
    /// </summary>
    private static readonly SuppressionDescriptor RDCDiagnostic = new(id: "SYSLIBSUPPRESS0003", suppressedDiagnosticId: RDCDiagnosticId, Justification);

    /// <inheritdoc/>
    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => [RUCDiagnostic, RDCDiagnostic];

    /// <inheritdoc/>
    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        // Don't suppress diagnostics unless the interceptor is running.
        if (!ProxyGenerator.AreInterceptorsEnabled(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions))
        {
            return;
        }

        if (!KnownSymbols.TryCreate(context.Compilation, out KnownSymbols? symbols))
        {
            return;
        }

        foreach (Diagnostic diagnostic in context.ReportedDiagnostics)
        {
            SuppressionDescriptor? targetSuppression = diagnostic.Id switch
            {
                RUCDiagnosticId => RUCDiagnostic,
                RDCDiagnosticId => RDCDiagnostic,
                _ => null,
            };

            if (targetSuppression is null)
            {
                // This suppressor only handles IL2026 and IL3050 diagnostics.
                continue;
            }

            Location location = diagnostic.AdditionalLocations.Count > 0
                ? diagnostic.AdditionalLocations[0]
                : diagnostic.Location;

            if (location.SourceTree is null)
            {
                continue;
            }

            SyntaxNode syntaxNode = location.SourceTree.GetRoot(context.CancellationToken).FindNode(location.SourceSpan);

            // The trim analyzer changed from warning on the InvocationExpression to the MemberAccessExpression in https://github.com/dotnet/runtime/pull/110086
            // In other words, the warning location went from from `{|Method1(arg1, arg2)|}` to `{|Method1|}(arg1, arg2)`
            // To account for this, we need to check if the location is an InvocationExpression or a child of an InvocationExpression.
            if ((syntaxNode as InvocationExpressionSyntax ?? syntaxNode.Parent as InvocationExpressionSyntax) is not InvocationExpressionSyntax invocation)
            {
                continue;
            }

            // Quickly weed out clearly irrelevant invocations.
            if (invocation is not { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Attach" } })
            {
                continue;
            }

            if (ProxyGenerator.TryGetInterceptInfo(invocation, context.GetSemanticModel(location.SourceTree), symbols, context.CancellationToken) is null)
            {
                continue;
            }

            // At this point, we can count on our source generator having generated a static method that intercepts this invocation.
            context.ReportSuppression(Suppression.Create(targetSuppression, diagnostic));
        }
    }
}
