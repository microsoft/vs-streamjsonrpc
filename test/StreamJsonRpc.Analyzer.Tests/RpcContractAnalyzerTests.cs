// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CodeFixVerifier<StreamJsonRpc.Analyzers.RpcContractAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class RpcContractAnalyzerTests
{
    [Fact]
    public async Task ReturnTypeAnalyzer()
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
                {|StreamJsonRpc0001:int|} MyMethod(CancellationToken cancellationToken);
            }
            """);
    }
}
