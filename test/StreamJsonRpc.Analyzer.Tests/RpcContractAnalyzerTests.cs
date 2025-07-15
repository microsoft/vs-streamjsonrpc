// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CodeFixVerifier<StreamJsonRpc.Analyzers.RpcContractAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class RpcContractAnalyzerTests
{
    [Fact]
    public async Task MethodReturnTypes()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcContract]
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
    public async Task InaccessibleInterface()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            internal partial class Wrapper
            {
                [RpcContract]
                private partial interface {|StreamJsonRpc0001:IMyRpc|}
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
                [RpcContract]
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
                [RpcContract]
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
            [RpcContract]
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
            [RpcContract]
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
            [RpcContract]
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
            [RpcContract]
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
            [RpcContract]
            partial interface {|StreamJsonRpc0015:IMyRpc|}<T>
            {
            }
            """);
    }
}
