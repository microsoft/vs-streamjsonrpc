// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CSharpSourceGeneratorVerifier<StreamJsonRpc.Analyzers.ProxyGenerator>;

public class GenerationTests
{
    [Fact]
    public async Task Public_NotNested()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcProxy]
            public interface IMyRpc
            {
                Task JustCancellationAsync(CancellationToken cancellationToken);
                ValueTask AnArgAndCancellationAsync(int arg, CancellationToken cancellationToken);
                Task<int> AddAsync(int a, int b, CancellationToken cancellationToken);
                Task<int> MultiplyAsync(int a, int b);
                void Start(string bah);
                void StartCancelable(string bah, CancellationToken token);
                IAsyncEnumerable<int> CountAsync(int start, int count, CancellationToken cancellationToken);
            }
            """);
    }

    [Fact]
    public async Task NestedInType()
    {
        await VerifyCS.RunDefaultAsync("""
            internal class Wrapper
            {
                [RpcProxy]
                public interface IMyRpc
                {
                    Task JustCancellationAsync(CancellationToken cancellationToken);
                }
            }
            """);
    }

    [Fact]
    public async Task NestedInTypeAndNamespace()
    {
        await VerifyCS.RunDefaultAsync("""
            namespace A;

            internal class Wrapper
            {
                [RpcProxy]
                public interface IMyRpc
                {
                    Task JustCancellationAsync(CancellationToken cancellationToken);
                }
            }
            """);
    }

    [Fact]
    public async Task NamesRequiredNamespaceQualifier()
    {
        await VerifyCS.RunDefaultAsync("""
            namespace A
            {
                [RpcProxy]
                public interface IMyRpc
                {
                    Task JustCancellationAsync(CancellationToken cancellationToken);
                }
            }

            namespace B
            {
                [RpcProxy]
                public interface IMyRpc
                {
                    Task JustAnotherCancellationAsync(CancellationToken cancellationToken);
                }
            }
            """);
    }

    [Fact]
    public async Task NameRequiredContainingTypeQualifier()
    {
        await VerifyCS.RunDefaultAsync("""
            class A
            {
                [RpcProxy]
                public interface IMyRpc
                {
                    Task JustCancellationAsync(CancellationToken cancellationToken);
                }
            }
            
            class B
            {
                [RpcProxy]
                public interface IMyRpc
                {
                    Task JustAnotherCancellationAsync(CancellationToken cancellationToken);
                }
            }
            """);
    }

    [Fact]
    public async Task Interface_DerivesFromIDisposable()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcProxy]
            public interface IFoo : IDisposable
            {
                Task JustCancellationAsync(CancellationToken cancellationToken);
            }
            """);
    }

    [Fact]
    public async Task Interface_HasDisposeWithoutIDisposable()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcProxy]
            public interface IFoo
            {
                Task Dispose();
            }
            """);
    }

    [Fact]
    public async Task Interface_DerivesFromOthers()
    {
        await VerifyCS.RunDefaultAsync("""
            public interface IFoo
            {
                Task JustCancellationAsync(CancellationToken cancellationToken);
            }

            [RpcProxy]
            public interface IFoo2 : IFoo
            {
                Task JustAnotherCancellationAsync(CancellationToken cancellationToken);
            }
            """);
    }

    [Fact]
    public async Task Events()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcProxy]
            interface IFoo
            {
                event EventHandler MyEvent;
                event EventHandler<string> MyGenericEvent;
            }
            """);
    }

    [Fact]
    public async Task EmptyInterface()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcProxy]
            public interface IFoo
            {
            }
            """);
    }

    [Fact]
    public async Task Overloads()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcProxy]
            public interface IFoo
            {
                Task SayHi();
                Task SayHi(string name);
            }
            """);
    }
}
