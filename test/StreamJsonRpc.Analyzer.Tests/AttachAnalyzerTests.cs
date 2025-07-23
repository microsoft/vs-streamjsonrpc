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

    [Fact]
    public async Task AttachGeneric_Class()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            public class MyService
            {
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    {|StreamJsonRpc0004:rpc.Attach<MyService>()|};
                }
            }
            """);
    }

    [Fact]
    public async Task Attach_Struct()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            public struct MyService
            {
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    {|StreamJsonRpc0004:rpc.Attach(typeof(MyService))|};
                }
            }
            """);
    }

    [Fact]
    public async Task Attach_OpenGeneric()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
            public interface IMyService<T>
            {
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    object service = {|StreamJsonRpc0004:rpc.Attach(typeof(IMyService<>))|};
                }
            }
            """);
    }

    [Fact]
    public async Task Attach_ClosedGeneric()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
            public interface IMyService<T>
            {
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    object service = rpc.Attach(typeof(IMyService<int>));
                }
            }
            """);
    }

    [Fact]
    public async Task Attach_GenericTypeParameter()
    {
        await VerifyCS.VerifyAnalyzerAsync("""
            [JsonRpcContract]
            public interface IMyService<T>
            {
            }

            class Test
            {
                void Foo<T>(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    object service = rpc.Attach(typeof(IMyService<T>));
                }
            }
            """);
    }
}
