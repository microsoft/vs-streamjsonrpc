// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CodeFixVerifier<StreamJsonRpc.Analyzers.AttachAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class AttachAnalyzerTests
{
    [Fact]
    public async Task AttachGeneric_WithAttribute()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
            public partial interface IMyService
            {
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    IMyService service = rpc.Attach<IMyService>();
                }
            }
            """);
    }

    [Fact]
    public async Task AttachGeneric_WithoutAttribute()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            public interface IMyService
            {
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    IMyService service = {|StreamJsonRpc0003:rpc.Attach<IMyService>()|};
                }
            }
            """);
    }
}
