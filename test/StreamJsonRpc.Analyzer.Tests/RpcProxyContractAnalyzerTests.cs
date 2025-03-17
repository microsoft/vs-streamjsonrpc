// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CodeFixVerifier<StreamJsonRpc.Analyzers.RpcProxyContractAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class RpcProxyContractAnalyzerTests
{
    [Fact]
    public async Task UnsupportedReturnType()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcProxy]
            public interface IMyRpc
            {
                {|StreamJsonRpc0001:int|} MyMethod(CancellationToken cancellationToken);
            }
            """);
    }
}
