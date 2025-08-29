// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CodeFixVerifier<StreamJsonRpc.Analyzers.JsonRpcProxyAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class JsonRpcProxyAnalyzerTests
{
    [Fact]
    public async Task ProperUse()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
            [JsonRpcProxy<IMyRpcMarshalable<int>>]
            public partial interface IMyRpcMarshalable<T> : IDisposable
            {
            }

            [JsonRpcContract, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
            [JsonRpcProxy<IMyRpcContract<int>>]
            public partial interface IMyRpcContract<T>
            {
            }
            """);
    }

    [Fact]
    public async Task NotOnGenericInterface()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
            [{|StreamJsonRpc0030:JsonRpcProxy<IMyRpcMarshalable>|}]
            public partial interface IMyRpcMarshalable : IDisposable
            {
            }
            """);
    }

    [Fact]
    public async Task TypeArgIsNotClosedAppliedInterface()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
            [{|StreamJsonRpc0031:JsonRpcProxy<int>|}]
            public partial interface IMyRpcMarshalable<T>
            {
            }
            """);
    }

    [Fact]
    public async Task MissingContractAttribute()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [{|StreamJsonRpc0032:JsonRpcProxy<IMyRpcMarshalable<int>>|}]
            public partial interface IMyRpcMarshalable<T> : IDisposable
            {
            }

            [{|StreamJsonRpc0032:JsonRpcProxy<IMyRpcContract<int>>|}]
            public partial interface IMyRpcContract<T>
            {
            }
            """);
    }
}
