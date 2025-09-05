// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using StreamJsonRpc.Analyzers;

namespace StreamJsonRpc.Analyzers.GeneratorModels;

internal record MethodModel(string DeclaringInterfaceName, string Name, string ReturnType, RpcSpecialType ReturnSpecialType, string? ReturnTypeArg, ImmutableEquatableArray<ParameterModel> Parameters, string RpcMethodName) : FormattableModel
{
    internal bool TakesCancellationToken => this.Parameters.Length > 0 && this.Parameters[^1].SpecialType == RpcSpecialType.CancellationToken;

    internal ParameterModel? CancellationToken => this.TakesCancellationToken ? this.Parameters[^1] : null;

    /// <summary>
    /// Gets a span over the parameters that exclude the <see cref="CancellationToken"/>.
    /// </summary>
    internal ReadOnlyMemory<ParameterModel> DataParameters => this.Parameters.AsMemory()[..(this.Parameters.Length - (this.TakesCancellationToken ? 1 : 0))];

    internal int? UniqueSuffix { get; init; }

    private string NamedTypesFieldName => $"{this.Name}NamedArgumentDeclaredTypes{this.UniqueSuffix}";

    private string PositionalTypesFieldName => $"{this.Name}PositionalArgumentDeclaredTypes{this.UniqueSuffix}";

    private string TransformedMethodNameFieldName => $"transformed{this.Name}{this.UniqueSuffix}";

    private string? CancellationTokenExpression => this.CancellationToken?.Name;

    private string? ReturnExpression => this.ReturnSpecialType switch
    {
        RpcSpecialType.Void => string.Empty,
        RpcSpecialType.Task => "result",
        RpcSpecialType.ValueTask => $"new {this.ReturnType}(result)",
        RpcSpecialType.IAsyncEnumerable => $"global::StreamJsonRpc.Reflection.CodeGenHelpers.CreateAsyncEnumerableProxy(result, {this.CancellationTokenExpression ?? "default"})",
        _ => null,
    };

    [MemberNotNullWhen(true, nameof(ReturnExpression))]
    private bool IsSupported => this.ReturnExpression is not null;

    internal override void WriteFields(SourceWriter writer)
    {
        writer.WriteLine($$"""

            private static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Type> {{this.NamedTypesFieldName}} = new global::System.Collections.Generic.Dictionary<string, global::System.Type>
            {
            """);
        writer.Indentation++;
        foreach (ParameterModel parameter in this.DataParameters.Span)
        {
            writer.WriteLine($"""["{parameter.Name}"] = typeof({parameter.TypeNoNullRefAnnotations}),""");
        }

        writer.Indentation--;
        writer.WriteLine("""
            };
            """);

        writer.WriteLine($$"""

                private static readonly global::System.Collections.Generic.IReadOnlyList<global::System.Type> {{this.PositionalTypesFieldName}} = new global::System.Collections.Generic.List<global::System.Type>
                {
                """);
        writer.Indentation++;
        foreach (ParameterModel parameter in this.DataParameters.Span)
        {
            writer.WriteLine($"""typeof({parameter.TypeNoNullRefAnnotations}),""");
        }

        writer.Indentation--;
        writer.WriteLine("""
                };
                """);

        if (this.IsSupported)
        {
            writer.WriteLine($"""

                    private string? {this.TransformedMethodNameFieldName};
                    """);
        }
    }

    internal override void WriteMethods(SourceWriter writer)
    {
        // The possible methods we invoke are as follows:
        // | Return type | Named args | Signature
        // | Task        | Yes        | Task InvokeWithParameterObjectAsync(string targetName, object? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes, CancellationToken cancellationToken)
        // | Task<T>     | Yes        | Task<TResult> InvokeWithParameterObjectAsync<TResult>(string targetName, object? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes, CancellationToken cancellationToken)
        // | void        | Yes        | Task NotifyWithParameterObjectAsync(string targetName, object? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes)
        // | Task        | No         | Task InvokeWithCancellationAsync(string targetName, IReadOnlyList<object?>? arguments, IReadOnlyList<Type> argumentDeclaredTypes, CancellationToken cancellationToken)
        // | Task<T>     | No         | Task<TResult> InvokeWithCancellationAsync<TResult>(string targetName, IReadOnlyList<object?>? arguments, IReadOnlyList<Type>? argumentDeclaredTypes, CancellationToken cancellationToken)
        // | void        | No         | Task NotifyAsync(string targetName, object?[]? arguments, IReadOnlyList<Type>? argumentDeclaredTypes)
        string returnTypeArg = this.ReturnTypeArg is null ? string.Empty :
            this.ReturnSpecialType == RpcSpecialType.IAsyncEnumerable ? $"<{this.ReturnType}>" :
            $"<{this.ReturnTypeArg}>";
        string? namedArgsInvocationMethodName = this.ReturnSpecialType switch
        {
            RpcSpecialType.Void => "NotifyWithParameterObjectAsync",
            _ => $"InvokeWithParameterObjectAsync{returnTypeArg}",
        };
        string? positionalArgsInvocationMethodName = this.ReturnSpecialType switch
        {
            RpcSpecialType.Void => "NotifyAsync",
            _ => $"InvokeWithCancellationAsync{returnTypeArg}",
        };

        string positionalArgs = "[" + string.Join(", ", this.DataParameters.Select(p => p.Name)) + "]";
        string namedArgs = "ConstructNamedArgs";

        string cancellationArg = this.ReturnSpecialType == RpcSpecialType.Void ? string.Empty : $", {this.CancellationToken?.Name ?? "default"}";

        writer.WriteLine($$"""

                {{this.ReturnType}} {{this.DeclaringInterfaceName}}.{{this.Name}}({{string.Join(", ", this.Parameters.Select(p => $"{p.Type} {p.Name}"))}})
                {
                """);

        writer.Indentation++;
        if (!this.IsSupported)
        {
            // StreamJsonRpc0011
            writer.WriteLine($"""
                    throw new global::System.NotSupportedException($"The return type '{this.ReturnType}' is not supported by the generated proxy method.");
                    """);
        }
        else
        {
            writer.WriteLine($$"""
                    if (this.IsDisposed) throw new global::System.ObjectDisposedException(this.GetType().FullName);

                    this.OnCallingMethod("{{this.Name}}");
                    string rpcMethodName = this.{{this.TransformedMethodNameFieldName}} ??= this.TransformMethodName("{{this.RpcMethodName}}", typeof({{this.DeclaringInterfaceName}}));
                    global::System.Threading.Tasks.Task{{returnTypeArg}} result = this.Options.ServerRequiresNamedArguments ?
                        this.JsonRpc.{{namedArgsInvocationMethodName}}(rpcMethodName, {{namedArgs}}(), {{this.NamedTypesFieldName}}{{cancellationArg}}) :
                        this.JsonRpc.{{positionalArgsInvocationMethodName}}(rpcMethodName, {{positionalArgs}}, {{this.PositionalTypesFieldName}}{{cancellationArg}});
                    this.OnCalledMethod("{{this.Name}}");

                    return {{this.ReturnExpression}};
                    """);

            writer.WriteLine($$"""

                global::System.Collections.Generic.Dictionary<string, object?> {{namedArgs}}()
                    => new()
                    {
                """);
            writer.Indentation += 2;
            foreach (ParameterModel parameter in this.DataParameters.Span)
            {
                writer.WriteLine($@"[""{parameter.Name}""] = {parameter.Name},");
            }

            writer.Indentation--;
            writer.WriteLine("};");
            writer.Indentation--;
        }

        writer.Indentation--;
        writer.WriteLine("""
                }
                """);
    }

    internal static MethodModel Create(IMethodSymbol method, KnownSymbols symbols)
    {
        AttributeData? methodShapeAttribute = method.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, symbols.MethodShapeAttribute));
        AttributeData? jsonRpcMethodAttribute = method.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, symbols.JsonRpcMethodAttribute));

        string rpcMethodName = method.Name;

        // If the method has a MethodShape attribute, prefer its name.
        if (methodShapeAttribute?.NamedArguments.FirstOrDefault(kv => kv.Key == Types.MethodShapeAttribute.NameProperty).Value is { Value: string methodShapeName })
        {
            rpcMethodName = methodShapeName;
        }

        // If the method has a JsonRpcMethod attribute, prefer its name above all.
        if (jsonRpcMethodAttribute is { ConstructorArguments: [{ Value: string jsonRpcMethodName }, ..] })
        {
            rpcMethodName = jsonRpcMethodName;
        }

        return new MethodModel(
            method.ContainingType.ToDisplayString(ProxyGenerator.FullyQualifiedWithNullableFormat),
            method.Name,
            method.ReturnType.ToDisplayString(ProxyGenerator.FullyQualifiedWithNullableFormat),
            ProxyGenerator.ClassifySpecialType(method.ReturnType, symbols),
            method.ReturnType is INamedTypeSymbol { IsGenericType: true, TypeArguments: [ITypeSymbol typeArg] } ? typeArg.ToDisplayString(ProxyGenerator.FullyQualifiedWithNullableFormat) : null,
            new([.. method.Parameters.Select(p => ParameterModel.Create(p, symbols))]),
            rpcMethodName);
    }
}
