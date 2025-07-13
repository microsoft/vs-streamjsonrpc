// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CodeFixVerifier<StreamJsonRpc.Analyzers.AllowAddingMembersLaterAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class AllowAddingMembersLaterAnalyzerTests
{
    [Fact]
    public async Task BothAttributes()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcContract, AllowAddingMembersLater]
            public partial interface IMyRpc
            {
            }
            """);
    }

    [Fact]
    public async Task OnlyAllowAddingMembersLater()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [{|StreamJsonRpc0100:AllowAddingMembersLater|}]
            public partial interface IMyRpc
            {
            }
            """);
    }
}
