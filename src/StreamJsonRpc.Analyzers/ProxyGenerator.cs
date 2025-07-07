// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1602 // Enumeration items should be documented

using Microsoft.CodeAnalysis;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// Source generator for StreamJsonRpc proxies.
/// </summary>
[Generator(LanguageNames.CSharp)]
public class ProxyGenerator : IIncrementalGenerator
{
    private static readonly SymbolDisplayFormat FullyQualifiedWithNullableFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>
    /// The various special types that the generator must recognize.
    /// </summary>
    internal enum SpecialType
    {
        Other,
        Void,
        Task,
        ValueTask,
        IAsyncEnumerable,
        CancellationToken,
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<DataModel?> proxyProvider = context.SyntaxProvider.ForAttributeWithMetadataName(
            Types.RpcContractAttribute.FullName,
            (node, cancellationToken) => true,
            (context, cancellationToken) =>
            {
                if (context.TargetSymbol is not INamedTypeSymbol iface)
                {
                    return null;
                }

                if (!KnownSymbols.TryCreate(context.SemanticModel.Compilation, out KnownSymbols? symbols))
                {
                    return null;
                }

                // Skip inaccessible interfaces.
                if (context.TargetSymbol.DeclaredAccessibility < Accessibility.Internal)
                {
                    // Reported by StreamJsonRpc0002
                    return null;
                }

                int methodIndex = 0;
                ImmutableEquatableArray<MethodModel> methods = new([..
                    iface.GetAllMembers()
                    .OfType<IMethodSymbol>()
                    .Where(m => m.AssociatedSymbol is null && !SymbolEqualityComparer.Default.Equals(m.ContainingType, symbols.IDisposable))
                    .Select(method => MethodModel.Create(method, symbols, ++methodIndex))]);

                ImmutableEquatableArray<EventModel?> events = new([..
                    iface.GetAllMembers()
                    .OfType<IEventSymbol>()
                    .Select(evt => EventModel.Create(evt, symbols))]);

                string fileNamePrefix = context.TargetSymbol.ToDisplayString(GenerationHelpers.QualifiedNameOnlyFormat);
                return new DataModel(
                    fileNamePrefix,
                    context.TargetSymbol.ToDisplayString(FullyQualifiedWithNullableFormat),
                    methods,
                    events);
            });

        context.RegisterSourceOutput(proxyProvider, this.GenerateProxy);
    }

    internal static SpecialType ClassifySpecialType(ITypeSymbol type, KnownSymbols symbols)
    {
        return type as INamedTypeSymbol switch
        {
            { SpecialType: Microsoft.CodeAnalysis.SpecialType.System_Void } => SpecialType.Void,
            { IsGenericType: true } namedType when Equal(namedType.ConstructedFrom, symbols.TaskOfT) => SpecialType.Task,
            { IsGenericType: true } namedType when Equal(namedType.ConstructedFrom, symbols.ValueTaskOfT) => SpecialType.ValueTask,
            { IsGenericType: true } namedType when Equal(namedType.ConstructedFrom, symbols.IAsyncEnumerableOfT) => SpecialType.IAsyncEnumerable,
            { IsGenericType: false } namedType when Equal(type, symbols.Task) => SpecialType.Task,
            { IsGenericType: false } namedType when Equal(type, symbols.ValueTask) => SpecialType.ValueTask,
            { IsGenericType: false } namedType when Equal(type, symbols.CancellationToken) => SpecialType.CancellationToken,
            _ => SpecialType.Other,
        };

        static bool Equal(ITypeSymbol candidate, ITypeSymbol? standard) => standard is not null && SymbolEqualityComparer.Default.Equals(candidate, standard);
    }

    private void GenerateProxy(SourceProductionContext context, DataModel? model)
    {
        if (model is null)
        {
            return;
        }

        // TODO: consider declaring the proxy type with equivalent visibility as the interface,
        //       since a public interface needs a publicly accessible proxy.
        //       Otherwise Reflection is required to access the type.
        SourceWriter writer = new();
        writer.WriteLine($$"""
            // <auto-generated/>

            #nullable enable

            [assembly: global::StreamJsonRpc.Reflection.RpcProxyMappingAttribute(typeof({{model.InterfaceName}}), typeof(global::StreamJsonRpc.Proxies.{{model.ProxyName}}))]

            namespace StreamJsonRpc.Proxies;

            [global::System.CodeDom.Compiler.GeneratedCodeAttribute("{{ThisAssembly.AssemblyName}}", "{{ThisAssembly.AssemblyFileVersion}}")]
            internal class {{model.ProxyName}} : {{model.InterfaceName}}, global::StreamJsonRpc.Reflection.IJsonRpcClientProxyInternal
            {
            """);

        writer.Indentation++;
        model.WriteFields(writer, model);
        model.WriteConstructor(writer, model);
        model.WriteEvents(writer, model);
        model.WriteProperties(writer, model);
        model.WriteMethods(writer, model);
        model.WriteNestedTypes(writer, model);
        writer.Indentation--;

        writer.WriteLine("}");

        context.AddSource($"{model.FileNamePrefix}.g.cs", writer.ToSourceText());
    }

    private abstract record FormattableModel
    {
        public virtual void WriteFields(SourceWriter writer, DataModel ifaceModel)
        {
        }

        public virtual void WriteProperties(SourceWriter writer, DataModel ifaceModel)
        {
        }

        public virtual void WriteHookupStatements(SourceWriter writer, DataModel ifaceModel)
        {
        }

        public virtual void WriteEvents(SourceWriter writer, DataModel ifaceModel)
        {
        }

        public virtual void WriteMethods(SourceWriter writer, DataModel ifaceModel)
        {
        }

        public virtual void WriteNestedTypes(SourceWriter writer, DataModel ifaceModel)
        {
        }
    }

    private record DataModel(string FileNamePrefix, string InterfaceName, ImmutableEquatableArray<MethodModel> Methods, ImmutableEquatableArray<EventModel?> Events) : FormattableModel
    {
        public string ProxyName { get; } = $"{FileNamePrefix.Replace('.', '_')}_Proxy";

        internal string JsonRpcFieldName => "client";

        internal string OptionsFieldName => "options";

        internal string MarshaledObjectHandleFieldName => "marshaledObjectHandle";

        internal string OnDisposeFieldName => "onDispose";

        private IEnumerable<FormattableModel> FormattableElements => this.Methods.Cast<FormattableModel>().Concat(this.Events.Where(e => e is not null))!;

        public void WriteConstructor(SourceWriter writer, DataModel ifaceModel)
        {
            writer.WriteLine($$"""
                public {{ifaceModel.ProxyName}}(global::StreamJsonRpc.JsonRpc client, global::StreamJsonRpc.JsonRpcProxyOptions options, long? marshaledObjectHandle, global::System.Action? onDispose)
                {
                    this.{{this.JsonRpcFieldName}} = client ?? throw new global::System.ArgumentNullException(nameof(client));
                    this.{{this.OptionsFieldName}} = options ?? throw new global::System.ArgumentNullException(nameof(options));
                    this.{{this.MarshaledObjectHandleFieldName}} = marshaledObjectHandle;
                    this.{{this.OnDisposeFieldName}} = onDispose;
                """);

            writer.Indentation++;
            foreach (FormattableModel formattable in this.FormattableElements)
            {
                formattable.WriteHookupStatements(writer, ifaceModel);
            }

            writer.Indentation--;
            writer.WriteLine("""
                }
                """);
        }

        public override void WriteEvents(SourceWriter writer, DataModel ifaceModel)
        {
            writer.WriteLine("""

                event global::System.EventHandler<string> global::StreamJsonRpc.Reflection.IJsonRpcClientProxyInternal.CallingMethod
                {
                    add => this.callingMethod += value;
                    remove => this.callingMethod -= value;
                }
                
                event global::System.EventHandler<string> global::StreamJsonRpc.Reflection.IJsonRpcClientProxyInternal.CalledMethod
                {
                    add => this.calledMethod += value;
                    remove => this.calledMethod -= value;
                }
                """);

            foreach (FormattableModel formattable in this.FormattableElements)
            {
                formattable.WriteEvents(writer, ifaceModel);
            }
        }

        public override void WriteHookupStatements(SourceWriter writer, DataModel ifaceModel)
        {
            foreach (FormattableModel formattable in this.FormattableElements)
            {
                formattable.WriteHookupStatements(writer, ifaceModel);
            }
        }

        public override void WriteMethods(SourceWriter writer, DataModel ifaceModel)
        {
            writer.WriteLine($$"""

                void global::System.IDisposable.Dispose()
                {
                    if (this.disposed)
                    {
                        return;
                    }
                    this.disposed = true;
                
                    if (this.{{this.OnDisposeFieldName}} is not null)
                    {
                        this.{{this.OnDisposeFieldName}}();
                    }
                    else
                    {
                        {{this.JsonRpcFieldName}}.Dispose();
                    }
                }
                """);

            foreach (FormattableModel formattable in this.FormattableElements)
            {
                formattable.WriteMethods(writer, ifaceModel);
            }
        }

        public override void WriteFields(SourceWriter writer, DataModel ifaceModel)
        {
            writer.WriteLine($$"""
                private readonly global::StreamJsonRpc.JsonRpc {{this.JsonRpcFieldName}};
                private readonly global::StreamJsonRpc.JsonRpcProxyOptions {{this.OptionsFieldName}};
                private readonly global::System.Action? {{this.OnDisposeFieldName}};
                private readonly long? {{this.MarshaledObjectHandleFieldName}};

                private global::System.EventHandler<string>? callingMethod;
                private global::System.EventHandler<string>? calledMethod;
                private bool disposed;
                """);

            foreach (FormattableModel formattable in this.FormattableElements)
            {
                formattable.WriteFields(writer, ifaceModel);
            }
        }

        public override void WriteProperties(SourceWriter writer, DataModel ifaceModel)
        {
            writer.WriteLine($$"""

                global::StreamJsonRpc.JsonRpc global::StreamJsonRpc.IJsonRpcClientProxy.JsonRpc => this.{{this.JsonRpcFieldName}};
                
                public bool IsDisposed => this.disposed || this.{{this.JsonRpcFieldName}}.IsDisposed;
                
                long? global::StreamJsonRpc.Reflection.IJsonRpcClientProxyInternal.MarshaledObjectHandle => this.{{this.MarshaledObjectHandleFieldName}};
                """);

            foreach (FormattableModel formattable in this.FormattableElements)
            {
                formattable.WriteProperties(writer, ifaceModel);
            }
        }

        public override void WriteNestedTypes(SourceWriter writer, DataModel ifaceModel)
        {
            foreach (FormattableModel formattable in this.FormattableElements)
            {
                formattable.WriteNestedTypes(writer, ifaceModel);
            }
        }
    }

    private record MethodModel(string DeclaringInterfaceName, string Name, string ReturnType, SpecialType ReturnSpecialType, string? ReturnTypeArg, ImmutableEquatableArray<ParameterModel> Parameters, int UniqueSuffix, string RpcMethodName) : FormattableModel
    {
        internal bool TakesCancellationToken => this.Parameters.Length > 0 && this.Parameters[^1].SpecialType == SpecialType.CancellationToken;

        internal ParameterModel? CancellationToken => this.TakesCancellationToken ? this.Parameters[^1] : null;

        /// <summary>
        /// Gets a span over the parameters that exclude the <see cref="CancellationToken"/>.
        /// </summary>
        internal ReadOnlyMemory<ParameterModel> DataParameters => this.Parameters.AsMemory()[..(this.Parameters.Length - (this.TakesCancellationToken ? 1 : 0))];

        private string NamedArgsTypeName { get; } = $"{Name}NamedArgs{UniqueSuffix}";

        private string PositionalTypesFieldName { get; } = $"{Name}PositionalArgumentDeclaredTypes{UniqueSuffix}";

        public override void WriteFields(SourceWriter writer, DataModel ifaceModel)
        {
            writer.WriteLine($$"""

                private static readonly global::System.Collections.Generic.IReadOnlyList<global::System.Type> {{this.PositionalTypesFieldName}} = new global::System.Collections.Generic.List<global::System.Type>
                {
                """);
            writer.Indentation++;
            foreach (ParameterModel parameter in this.DataParameters.Span)
            {
                writer.WriteLine($"""typeof({parameter.TypeNoNullRefAnnotaions}),""");
            }

            writer.Indentation--;
            writer.WriteLine("""
                };
                """);
        }

        public override void WriteMethods(SourceWriter writer, DataModel ifaceModel)
        {
            string? cancellationTokenExpression = this.CancellationToken?.Name;
            string? returnExpression = this.ReturnSpecialType switch
            {
                SpecialType.Void => string.Empty,
                SpecialType.Task => "result",
                SpecialType.ValueTask => $"new {this.ReturnType}(result)",
                SpecialType.IAsyncEnumerable => $"global::StreamJsonRpc.Reflection.CodeGenHelpers.CreateAsyncEnumerableProxy(result, {cancellationTokenExpression ?? "default"})",
                _ => null,
            };

            // The possible methods we invoke are as follows:
            // | Return type | Named args | Signature
            // | Task        | Yes        | Task InvokeWithParameterObjectAsync(string targetName, object? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes, CancellationToken cancellationToken)
            // | Task<T>     | Yes        | Task<TResult> InvokeWithParameterObjectAsync<TResult>(string targetName, object? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes, CancellationToken cancellationToken)
            // | void        | Yes        | Task NotifyWithParameterObjectAsync(string targetName, object? argument, IReadOnlyDictionary<string, Type>? argumentDeclaredTypes)
            // | Task        | No         | Task InvokeWithCancellationAsync(string targetName, IReadOnlyList<object?>? arguments, IReadOnlyList<Type> argumentDeclaredTypes, CancellationToken cancellationToken)
            // | Task<T>     | No         | Task<TResult> InvokeWithCancellationAsync<TResult>(string targetName, IReadOnlyList<object?>? arguments, IReadOnlyList<Type>? argumentDeclaredTypes, CancellationToken cancellationToken)
            // | void        | No         | Task NotifyAsync(string targetName, object?[]? arguments, IReadOnlyList<Type>? argumentDeclaredTypes)
            string returnTypeArg = this.ReturnTypeArg is null ? string.Empty :
                this.ReturnSpecialType == SpecialType.IAsyncEnumerable ? $"<{this.ReturnType}>" :
                $"<{this.ReturnTypeArg}>";
            string? namedArgsInvocationMethodName = this.ReturnSpecialType switch
            {
                SpecialType.Void => "NotifyWithParameterObjectAsync",
                _ => $"InvokeWithParameterObjectAsync{returnTypeArg}",
            };
            string? positionalArgsInvocationMethodName = this.ReturnSpecialType switch
            {
                SpecialType.Void => "NotifyAsync",
                _ => $"InvokeWithCancellationAsync{returnTypeArg}",
            };

            string positionalArgs = "[" + string.Join(", ", this.DataParameters.Select(p => p.Name)) + "]";
            string namedArgs = $"new {this.NamedArgsTypeName}({string.Join(", ", this.DataParameters.Select(p => p.Name))})";

            string cancellationArg = this.ReturnSpecialType == SpecialType.Void ? string.Empty : $", {this.CancellationToken?.Name ?? "default"}";

            writer.WriteLine($$"""

                {{this.ReturnType}} {{this.DeclaringInterfaceName}}.{{this.Name}}({{string.Join(", ", this.Parameters.Select(p => $"{p.Type} {p.Name}"))}})
                {
                """);

            writer.Indentation++;
            if (returnExpression is null)
            {
                // StreamJsonRpc0001
                writer.WriteLine($"""
                    throw new global::System.NotSupportedException($"The return type '{this.ReturnType}' is not supported by the generated proxy method.");
                    """);
            }
            else
            {
                writer.WriteLine($$"""
                    if (this.IsDisposed) throw new global::System.ObjectDisposedException(nameof({{ifaceModel.ProxyName}}));

                    this.callingMethod?.Invoke(this, "{{this.Name}}");
                    string rpcMethodName = this.{{ifaceModel.OptionsFieldName}}.MethodNameTransform("{{this.RpcMethodName}}");
                    global::System.Threading.Tasks.Task{{returnTypeArg}} result = this.{{ifaceModel.OptionsFieldName}}.ServerRequiresNamedArguments ?
                        this.{{ifaceModel.JsonRpcFieldName}}.{{namedArgsInvocationMethodName}}(rpcMethodName, {{namedArgs}}, null{{cancellationArg}}) :
                        this.{{ifaceModel.JsonRpcFieldName}}.{{positionalArgsInvocationMethodName}}(rpcMethodName, {{positionalArgs}}, {{this.PositionalTypesFieldName}}{{cancellationArg}});
                    this.calledMethod?.Invoke(this, "{{this.Name}}");

                    return {{returnExpression}};
                    """);
            }

            writer.Indentation--;
            writer.WriteLine("""
                }
                """);
        }

        public override void WriteNestedTypes(SourceWriter writer, DataModel ifaceModel)
        {
            writer.WriteLine($$"""

                private readonly struct {{this.NamedArgsTypeName}}
                {
                    public {{this.NamedArgsTypeName}}({{string.Join(", ", this.DataParameters.Select(p => $"{p.Type} {p.Name}"))}})
                    {
                """);
            writer.Indentation += 2;
            foreach (ParameterModel p in this.DataParameters.Span)
            {
                writer.WriteLine($"""
                    this.{p.Name} = {p.Name};
                    """);
            }

            writer.Indentation--;
            writer.WriteLine($$"""
                }
                """);

            foreach (ParameterModel p in this.DataParameters.Span)
            {
                writer.WriteLine($$"""

                    public readonly {{p.Type}} {{p.Name}};
                    """);
            }

            writer.Indentation--;
            writer.WriteLine($$"""
                }
                """);
        }

        internal static MethodModel Create(IMethodSymbol method, KnownSymbols symbols, int uniqueSuffix)
        {
            string rpcMethodName = method.Name;
            if (method.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, symbols.JsonRpcMethodAttribute)) is { } rpcMethodAttribute)
            {
                // If the method has a JsonRpcMethod attribute, use its name.
                if (rpcMethodAttribute.ConstructorArguments.Length > 0 &&
                    rpcMethodAttribute.ConstructorArguments[0].Value is string name)
                {
                    rpcMethodName = name;
                }
            }

            return new MethodModel(
                method.ContainingType.ToDisplayString(FullyQualifiedWithNullableFormat),
                method.Name,
                method.ReturnType.ToDisplayString(FullyQualifiedWithNullableFormat),
                ClassifySpecialType(method.ReturnType, symbols),
                method.ReturnType is INamedTypeSymbol { IsGenericType: true, TypeArguments: [ITypeSymbol typeArg] } ? typeArg.ToDisplayString(FullyQualifiedWithNullableFormat) : null,
                new([.. method.Parameters.Select(p => ParameterModel.Create(p, symbols))]),
                uniqueSuffix,
                rpcMethodName);
        }
    }

    private record ParameterModel(string Name, string Type, string TypeNoNullRefAnnotaions, SpecialType SpecialType)
    {
        internal static ParameterModel Create(IParameterSymbol parameter, KnownSymbols symbols)
            => new(parameter.Name, parameter.Type.ToDisplayString(FullyQualifiedWithNullableFormat), parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), ClassifySpecialType(parameter.Type, symbols));
    }

    private record EventModel(string Name, string DelegateType, string EventArgsType) : FormattableModel
    {
        public override void WriteHookupStatements(SourceWriter writer, DataModel ifaceModel)
        {
            writer.WriteLine($"""
                this.{ifaceModel.JsonRpcFieldName}.AddLocalRpcMethod(options.EventNameTransform("{this.Name}"), this.On{this.Name});
                """);
        }

        public override void WriteEvents(SourceWriter writer, DataModel ifaceModel)
        {
            writer.WriteLine($$"""

                public event {{this.DelegateType}}? {{this.Name}};

                protected virtual void On{{this.Name}}({{this.EventArgsType}} args) => this.{{this.Name}}?.Invoke(this, args);
                """);
        }

        internal static EventModel? Create(IEventSymbol evt, KnownSymbols symbols)
        {
            if (evt.Type is not INamedTypeSymbol { DelegateInvokeMethod: { } invokeMethod })
            {
                return null;
            }

            return new EventModel(evt.Name, evt.Type.ToDisplayString(FullyQualifiedWithNullableFormat), invokeMethod.Parameters[1].Type.ToDisplayString(FullyQualifiedWithNullableFormat));
        }
    }
}
