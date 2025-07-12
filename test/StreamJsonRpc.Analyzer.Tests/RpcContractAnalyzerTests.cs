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
            public interface IMyRpc
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
            internal class Wrapper
            {
                [RpcContract]
                private interface {|StreamJsonRpc0010:IMyRpc|}
                {
                }
            }
            """);
    }

    [Fact]
    public async Task InternalInterface()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            internal class Wrapper
            {
                [RpcContract]
                internal interface IMyRpc
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
            interface IMyRpc
            {
                int {|StreamJsonRpc0012:Count|} { get; }
                void {|StreamJsonRpc0013:Add|}<T>(T item);
            }
            """);
    }

    [Fact]
    public async Task CancellationTokenPositions()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcContract]
            interface IMyRpc
            {
                Task AddAsync(int a, int b, CancellationToken token);
                Task SubtractAsync(int a, CancellationToken {|StreamJsonRpc0014:token|}, int b);
                Task DivideAsync(CancellationToken {|StreamJsonRpc0014:token|}, int a, int b);
            }
            """);
    }
}
