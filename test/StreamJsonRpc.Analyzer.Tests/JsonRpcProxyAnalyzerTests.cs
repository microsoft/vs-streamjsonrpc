// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CodeFixVerifier<StreamJsonRpc.Analyzers.JsonRpcProxyAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class JsonRpcProxyAnalyzerTests
{
    [Fact]
    public async Task ProperUse()
    {
#if POLYTYPE
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
#else
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcMarshalable]
            [JsonRpcProxy<IMyRpcMarshalable<int>>]
            public partial interface IMyRpcMarshalable<T> : IDisposable
            {
            }

            [JsonRpcContract]
            [JsonRpcProxy<IMyRpcContract<int>>]
            public partial interface IMyRpcContract<T>
            {
            }
            """);
#endif
    }

    [Fact]
    public async Task NotOnGenericInterface()
    {
#if POLYTYPE
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
            [{|StreamJsonRpc0030:JsonRpcProxy<IMyRpcMarshalable>|}]
            public partial interface IMyRpcMarshalable : IDisposable
            {
            }
            """);
#else
        await VerifyCS.VerifyAnalyzerAsync("""
            [RpcMarshalable]
            [{|StreamJsonRpc0030:JsonRpcProxy<IMyRpcMarshalable>|}]
            public partial interface IMyRpcMarshalable : IDisposable
            {
            }
            """);
#endif
    }

    [Fact]
    public async Task TypeArgIsNotClosedAppliedInterface()
    {
#if POLYTYPE
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
            [{|StreamJsonRpc0031:JsonRpcProxy<int>|}]
            public partial interface IMyRpcMarshalable<T>
            {
            }
            """);
#else
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
            [{|StreamJsonRpc0031:JsonRpcProxy<int>|}]
            public partial interface IMyRpcMarshalable<T>
            {
            }
            """);
#endif
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
