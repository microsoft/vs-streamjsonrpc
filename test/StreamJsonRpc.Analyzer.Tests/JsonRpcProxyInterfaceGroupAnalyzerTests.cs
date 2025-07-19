// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CodeFixVerifier<StreamJsonRpc.Analyzers.JsonRpcProxyInterfaceGroupAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class JsonRpcProxyInterfaceGroupAnalyzerTests
{
    [Fact]
    public async Task EmptyGroup()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
            [JsonRpcProxyInterfaceGroup]
            partial interface IMyService
            {
            }
            """);
    }

    [Fact]
    public async Task TwoInterfacesAllGood()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
            [JsonRpcProxyInterfaceGroup(typeof(IMyService2))]
            partial interface IMyService
            {
            }

            [JsonRpcContract]
            partial interface IMyService2
            {
            }
            """);
    }

    [Fact]
    public async Task PrimaryInterfaceMissingContractAttribute()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [{|StreamJsonRpc0006:JsonRpcProxyInterfaceGroup(typeof(IMyService2))|}]
            partial interface IMyService
            {
            }

            [JsonRpcContract]
            partial interface IMyService2
            {
            }
            """);
    }

    [Fact]
    public async Task AlternateInterfaceMissingContractAttribute()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
            [{|StreamJsonRpc0006:JsonRpcProxyInterfaceGroup(typeof(IMyService2))|}]
            partial interface IMyService
            {
            }

            partial interface IMyService2
            {
            }
            """);
    }
}
