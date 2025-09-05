// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// An analyzer that encourages the use of IClientProxy.Is and JsonRpcExtensions.As{T}(IClientProxy)
/// over C# `is` and `as` operators for checking for RPC-implemented interfaces.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class OptionalInterfaceTypeCheckAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Diagnostic ID for StreamJsonRpc0050: Use IClientProxy.Is or JsonRpcExtensions.As.
    /// </summary>
    public const string UseIClientProxyId = "StreamJsonRpc0050";

    /// <summary>
    /// * Diagnostic for StreamJsonRpc0050: Use IClientProxy.Is or JsonRpcExtensions.As.
    /// </summary>
    public static readonly DiagnosticDescriptor UseIClientProxy = new(
        id: UseIClientProxyId,
        title: Strings.StreamJsonRpc0050_Title,
        messageFormat: Strings.StreamJsonRpc0050_MessageFormat,
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: AnalyzerUtilities.GetHelpLink(UseIClientProxyId));

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
        UseIClientProxy,
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
                    context => this.AnalyzeOperation(context, knownSymbols),
                    OperationKind.IsType,
                    OperationKind.Conversion); // as and cast operators.
            });
    }

    private void AnalyzeOperation(OperationAnalysisContext context, KnownSymbols knownSymbols)
    {
        (INamedTypeSymbol? targetType, INamedTypeSymbol? sourceType) = context.Operation switch
        {
            IIsTypeOperation { TypeOperand: INamedTypeSymbol target, ValueOperand.Type: INamedTypeSymbol src } isTypeOperation => (target, src),
            IConversionOperation { IsTryCast: true, Type: INamedTypeSymbol target, Operand.Type: INamedTypeSymbol src } conversionOperation => (target, src), // as
            IConversionOperation { IsTryCast: false, IsImplicit: false, OperatorMethod: null, Type: INamedTypeSymbol target, Operand.Type: INamedTypeSymbol src } => (target, src), // cast
            _ => (null, null),
        };

        if (targetType is not { TypeKind: TypeKind.Interface } || sourceType is not { TypeKind: TypeKind.Interface })
        {
            return;
        }

        // If either type is not an RpcMarshalable interface, bail out.
        if (!sourceType.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, knownSymbols.RpcMarshalableAttribute)) ||
            !targetType.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, knownSymbols.RpcMarshalableAttribute)))
        {
            return;
        }

        // If the target type isn't attributed with [RpcMarshalable(IsOptional = true)], bail out.
        AttributeData? targetAttr = targetType.GetAttributes().First(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, knownSymbols.RpcMarshalableAttribute));
        if (targetAttr.NamedArguments.FirstOrDefault(kvp => kvp.Key == nameof(Types.RpcMarshalableAttribute.IsOptional)).Value.Value is not true)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            UseIClientProxy,
            context.Operation.Syntax.GetLocation(),
            sourceType.ToDisplayString(GenerationHelpers.QualifiedNameOnlyFormat),
            targetType.ToDisplayString(GenerationHelpers.QualifiedNameOnlyFormat)));
    }
}
