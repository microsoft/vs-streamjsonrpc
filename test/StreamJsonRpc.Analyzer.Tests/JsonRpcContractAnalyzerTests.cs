// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using StreamJsonRpc.Analyzers;
using VerifyCS = CodeFixVerifier<StreamJsonRpc.Analyzers.JsonRpcContractAnalyzer, StreamJsonRpc.Analyzers.JsonRpcContractCodeFixProvider>;

public class JsonRpcContractAnalyzerTests
{
#if POLYTYPE
    private const string MethodShapes = ", TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)";
#else
    private const string MethodShapes = "";
#endif

    public JsonRpcContractAnalyzerTests()
    {
        JsonRpcContractCodeFixProvider.NormalizeLineEndings = true;
    }

    [Fact]
    public async Task OpenGenericRpcMarshalableNeedNotBePartial()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [RpcMarshalable{{MethodShapes}}]
            public interface IMyRpcMarshalable<T> : IDisposable
            {
            }
            """);
    }

#if POLYTYPE
    [Fact]
    public async Task OpenGenericRpcMarshalableShouldHaveTypeShapeMethods()
    {
        string source = """
            using System;
            using StreamJsonRpc;

            [RpcMarshalable]
            public interface {|StreamJsonRpc0008:IMyRpcMarshalable|}<T> : IDisposable
            {
            }
            """;
        string fixedSource = """
            using System;
            using PolyType;
            using StreamJsonRpc;

            [RpcMarshalable]
            [TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
            public interface IMyRpcMarshalable<T> : IDisposable
            {
            }
            """;
        await VerifyCS.VerifyCodeFixAsync(source, fixedSource);
    }

    [Fact]
    public async Task OpenGenericRpcMarshalableShouldHaveTypeShapeMethods_HasGenerateShape()
    {
        string source = """
            using System;
            using PolyType;
            using StreamJsonRpc;

            [RpcMarshalable]
            [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)] // Ineffective on an open generic
            public interface {|StreamJsonRpc0008:IMyRpcMarshalable|}<T> : IDisposable
            {
            }
            """;
        string fixedSource = """
            using System;
            using PolyType;
            using StreamJsonRpc;

            [RpcMarshalable]
            [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)] // Ineffective on an open generic
            [TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
            public interface IMyRpcMarshalable<T> : IDisposable
            {
            }
            """;
        await VerifyCS.VerifyCodeFixAsync(source, fixedSource);
    }
#endif

    [Fact]
    public async Task MethodReturnTypes()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [JsonRpcContract{{MethodShapes}}]
            public partial interface IMyRpc
            {
                Task<int> TaskOfTAsync();
                ValueTask<int> ValueTaskOfTAsync();
                ValueTask ValueTaskAsync();
                Task TaskAsync();
                void Notify();
                {|StreamJsonRpc0011:int|} MyMethod(CancellationToken cancellationToken);
            }
            """);
    }

    [Fact]
    public async Task InaccessibleInterface_Private()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            internal partial class Wrapper
            {
                [JsonRpcContract{{MethodShapes}}]
                private partial interface {|StreamJsonRpc0001:IMyRpc|}
                {
                }
            }
            """);
    }

    [Fact]
    public async Task InaccessibleInterface_Protected()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            public partial class Wrapper
            {
                [JsonRpcContract{{MethodShapes}}]
                protected partial interface {|StreamJsonRpc0001:IMyRpc|}
                {
                }
            }
            """);
    }

    [Fact]
    public async Task InternalInterface()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            internal partial class Wrapper
            {
                [JsonRpcContract{{MethodShapes}}]
                internal partial interface IMyRpc
                {
                }
            }
            """);
    }

    [Fact]
    public async Task NonPartialInterface()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            internal class Wrapper
            {
                [JsonRpcContract{{MethodShapes}}]
                internal interface {|StreamJsonRpc0002:IMyRpc|}
                {
                }
            }
            """);
    }

    [Fact]
    public async Task DisallowedMembers()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [JsonRpcContract{{MethodShapes}}]
            partial interface IMyRpc
            {
                event EventHandler Changed;
                event EventHandler<int> Updated;
                event CustomEvent {|StreamJsonRpc0016:Custom|};
                int {|StreamJsonRpc0012:Count|} { get; }
                void {|StreamJsonRpc0013:Add|}<T>(T item);
            }

            delegate void CustomEvent();
            """);
    }

#if NET
    [Fact]
    public async Task StaticMembersIgnored()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [JsonRpcContract{{MethodShapes}}]
            partial interface IMyRpc
            {
                static int StaticMethodsAreIgnored() => 3;
            }
            """);
    }
#endif

    [Fact]
    public async Task DisallowedMembers_InBaseInterface()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [JsonRpcContract{{MethodShapes}}]
            partial interface IMyRpc : {|StreamJsonRpc0013:{|StreamJsonRpc0012:{|StreamJsonRpc0016:IBase|}|}|}
            {
            }

            interface IBase
            {
                event EventHandler Changed;
                event EventHandler<int> Updated;
                event CustomEvent Custom;
                int Count { get; }
                void Add<T>(T item);
            }

            delegate void CustomEvent();
            """);
    }

    [Fact]
    public async Task DisallowedMembers_InBaseInterfaceTwoStepsAway()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [JsonRpcContract{{MethodShapes}}]
            partial interface IMyRpc : {|StreamJsonRpc0013:{|StreamJsonRpc0012:{|StreamJsonRpc0016:IBase2|}|}|}
                        {
            }

            interface IBase2 : IBase {}

            interface IBase
            {
                event EventHandler Changed;
                event EventHandler<int> Updated;
                event CustomEvent Custom;
                int Count { get; }
                void Add<T>(T item);
            }

            delegate void CustomEvent();
            """);
    }

    [Fact]
    public async Task CancellationTokenPositions()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [JsonRpcContract{{MethodShapes}}]
            partial interface IMyRpc
            {
                Task AddAsync(int a, int b, CancellationToken token);
                Task SubtractAsync(int a, CancellationToken {|StreamJsonRpc0014:token|}, int b);
                Task DivideAsync(CancellationToken {|StreamJsonRpc0014:token|}, int a, int b);
            }
            """);
    }

    [Fact]
    public async Task GenericInterface()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [JsonRpcContract{{MethodShapes}}]
            partial interface {|StreamJsonRpc0015:IMyRpc|}<T>
            {
            }
            """);
    }

    /// <summary>
    /// Generic interfaces <em>are</em> allowed for <see cref="RpcMarshalableAttribute"/> interfaces.
    /// </summary>
    [Fact]
    public async Task RpcMarshalable_GenericInterface()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [RpcMarshalable{{MethodShapes}}]
            partial interface IMyRpc<T> : IDisposable
            {
            }
            """);
    }

    [Fact]
    public async Task RpcMarshalable()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [RpcMarshalable{{MethodShapes}}]
            partial interface IMyRpc : IDisposable
            {
                Task SayHiAsync();
                event EventHandler {|StreamJsonRpc0012:Changed|};
            }
            """);
    }

    [Fact]
    public async Task RpcMarshalable_DisallowedMembers()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [RpcMarshalable{{MethodShapes}}]
            partial interface IMyRpc : IDisposable
            {
                event EventHandler {|StreamJsonRpc0012:Changed|};
                event EventHandler<int> {|StreamJsonRpc0012:Updated|};
                event CustomEvent {|StreamJsonRpc0012:Custom|};
                int {|StreamJsonRpc0012:Count|} { get; }
                void {|StreamJsonRpc0013:Add|}<T>(T item);
            }

            delegate void CustomEvent();
            """);
    }

    [Fact]
    public async Task RpcMarshalable_WithOptionalInterfaceAndNoAttribute()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [RpcMarshalable{{MethodShapes}}]
            [{|StreamJsonRpc0007:RpcMarshalableOptionalInterface(1, typeof(IMarshalableSubType1))|}]
            partial interface IMyRpc : IDisposable
            {
            }

            interface IMarshalableSubType1
            {
            }
            """);
    }

    [Fact]
    public async Task RpcMarshalable_WithOptionalInterfaceWithoutIsOptionalTrue()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [RpcMarshalable{{MethodShapes}}]
            [{|StreamJsonRpc0007:RpcMarshalableOptionalInterface(1, typeof(IMarshalableSubType1))|}]
            [{|StreamJsonRpc0007:RpcMarshalableOptionalInterface(2, typeof(IMarshalableSubType2))|}]
            partial interface IMyRpc : IDisposable
            {
            }

            [RpcMarshalable{{MethodShapes}}]
            partial interface IMarshalableSubType1 : IDisposable
            {
            }

            [RpcMarshalable(IsOptional = false){{MethodShapes}}]
            partial interface IMarshalableSubType2 : IDisposable
            {
            }
            """);
    }

    [Fact]
    public async Task RpcMarshalable_WithOptionalInterface()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [RpcMarshalable{{MethodShapes}}]
            [RpcMarshalableOptionalInterface(1, typeof(IMarshalableSubType1))]
            partial interface IMyRpc : IDisposable
            {
            }

            [RpcMarshalable(IsOptional = true){{MethodShapes}}]
            partial interface IMarshalableSubType1 : IDisposable
            {
            }
            """);
    }

    [Fact]
    public async Task RpcMarshalable_CallScopedNeedNotBeIDisposable()
    {
        await VerifyCS.VerifyAnalyzerAsync($$"""
            [RpcMarshalable(CallScopedLifetime = true){{MethodShapes}}]
            partial interface IMyRpc
            {
            }
            """);
    }

    [Fact]
    public async Task RpcMarshalable_MustDeriveFromIDisposable()
    {
        string source = $$"""
            using PolyType;

            [StreamJsonRpc.RpcMarshalable{{MethodShapes}}]
            partial interface {|StreamJsonRpc0005:IMyRpc|}
            {
            }
            """;
        string fixedSource = $$"""
            using System;
            using PolyType;

            [StreamJsonRpc.RpcMarshalable{{MethodShapes}}]
            partial interface IMyRpc: IDisposable
            {
            }
            """;
        await VerifyCS.VerifyCodeFixAsync(source, fixedSource);
    }

#if POLYTYPE
    [Fact]
    public async Task RpcInterfacesNeedMethodsIncludedInShape_Fixable()
    {
        string source = """
            using StreamJsonRpc;

            [JsonRpcContract]
            partial interface {|StreamJsonRpc0008:IRegularContract|}
            {
            }

            [RpcMarshalable]
            partial interface {|StreamJsonRpc0008:IMarshalable|} : System.IDisposable
            {
            }
            """;
        string fixedSource = """
            using PolyType;
            using StreamJsonRpc;

            [JsonRpcContract]
            [GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
            partial interface IRegularContract
            {
            }

            [RpcMarshalable]
            [TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
            partial interface IMarshalable : System.IDisposable
            {
            }
            """;
        await VerifyCS.VerifyCodeFixAsync(source, fixedSource);
    }

    /// <summary>
    /// The code fix provider isn't sophisticated enough to correct existing GenerateShape attributes.
    /// </summary>
    [Fact]
    public async Task RpcInterfacesNeedMethodsIncludedInShape_NotAutomaticallyFixable()
    {
        string source = """
            [JsonRpcContract, GenerateShape]
            partial interface {|StreamJsonRpc0008:IContractWithoutMethods|}
            {
            }

            [JsonRpcContract, TypeShape(IncludeMethods = MethodShapeFlags.None)]
            partial interface {|StreamJsonRpc0008:IContractWithoutMethodsExplicitly|}
            {
            }

            [JsonRpcContract, TypeShape(IncludeMethods = MethodShapeFlags.PublicStatic)]
            partial interface {|StreamJsonRpc0008:IContractWithOnlyStaticMethods|}
            {
            }

            [JsonRpcContract, TypeShape(IncludeMethods = MethodShapeFlags.AllPublic)]
            partial interface IContractWithAllPublicMethods
            {
            }
            """;
        await VerifyCS.VerifyAnalyzerAsync(source);
    }
#endif
}
