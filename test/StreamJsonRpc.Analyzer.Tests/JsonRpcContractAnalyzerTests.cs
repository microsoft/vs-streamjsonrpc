// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CodeFixVerifier<StreamJsonRpc.Analyzers.JsonRpcContractAnalyzer, StreamJsonRpc.Analyzers.JsonRpcContractCodeFixProvider>;

public class JsonRpcContractAnalyzerTests
{
    [Fact]
    public async Task MethodReturnTypes()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
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
        await VerifyCS.VerifyAnalyzerAsync("""
            internal partial class Wrapper
            {
                [JsonRpcContract]
                private partial interface {|StreamJsonRpc0001:IMyRpc|}
                {
                }
            }
            """);
    }

    [Fact]
    public async Task InaccessibleInterface_Protected()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            public partial class Wrapper
            {
                [JsonRpcContract]
                protected partial interface {|StreamJsonRpc0001:IMyRpc|}
                {
                }
            }
            """);
    }

    [Fact]
    public async Task InternalInterface()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            internal partial class Wrapper
            {
                [JsonRpcContract]
                internal partial interface IMyRpc
                {
                }
            }
            """);
    }

    [Fact]
    public async Task NonPartialInterface()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            internal class Wrapper
            {
                [JsonRpcContract]
                internal interface {|StreamJsonRpc0002:IMyRpc|}
                {
                }
            }
            """);
    }

    [Fact]
    public async Task DisallowedMembers()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
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

    [Fact]
    public async Task DisallowedMembers_InBaseInterface()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
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
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
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
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
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
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
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
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcMarshalable]
            partial interface IMyRpc<T> : IDisposable
            {
            }
            """);
    }

    [Fact]
    public async Task RpcMarshalable()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcMarshalable]
            partial interface IMyRpc : IDisposable
            {
                Task SayHiAsync();
                event EventHandler {|StreamJsonRpc0012:Changed|};
            }
            """);
    }

    [Fact]
    public async Task RpcMarshalable_CallScopedNeedNotBeIDisposable()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcMarshalable(CallScopedLifetime = true)]
            partial interface IMyRpc
            {
            }
            """);
    }

    [Fact]
    public async Task RpcMarshalable_MustDeriveFromIDisposable()
    {
        string source = """
            [StreamJsonRpc.RpcMarshalable]
            partial interface {|StreamJsonRpc0005:IMyRpc|}
            {
            }
            """;
        string fixedSource = """
            using System;

            [StreamJsonRpc.RpcMarshalable]
            partial interface IMyRpc : IDisposable
            {
            }
            """;
        await VerifyCS.VerifyCodeFixAsync(source, fixedSource);
    }
}
