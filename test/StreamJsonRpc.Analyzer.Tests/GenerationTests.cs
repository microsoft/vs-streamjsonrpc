// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CSharpSourceGeneratorVerifier<StreamJsonRpc.Analyzers.ProxyGenerator>;

public class GenerationTests
{
    [Fact]
    public async Task Public_NotNested()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
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
                [RpcContract]
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
                [RpcContract]
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
                [RpcContract]
                public interface IMyRpc
                {
                    Task JustCancellationAsync(CancellationToken cancellationToken);
                }
            }

            namespace B
            {
                [RpcContract]
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
                [RpcContract]
                public interface IMyRpc
                {
                    Task JustCancellationAsync(CancellationToken cancellationToken);
                }
            }
            
            class B
            {
                [RpcContract]
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
            [RpcContract]
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
            [RpcContract]
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

            [RpcContract]
            public interface IFoo2 : IFoo
            {
                Task JustAnotherCancellationAsync(CancellationToken cancellationToken);
            }
            """);
    }

    [Fact]
    public async Task Interface_DerivesFromOthersWithRedundantMethods()
    {
        await VerifyCS.RunDefaultAsync("""
            public interface ICalc1
            {
                Task<int> AddAsync(int a, int b);
            }

            public interface ICalc2
            {
                Task<int> AddAsync(int a, int b);
            }

            [RpcContract]
            public interface ICalc : ICalc1, ICalc2
            {
            }
            """);
    }

    [Fact(Skip = "Does not yet work.")]
    public async Task Interface_DerivesFromOthersWithRedundantEvents()
    {
        await VerifyCS.RunDefaultAsync("""
            public interface ICalc1
            {
                event EventHandler Changed;
            }

            public interface ICalc2
            {
                event EventHandler Changed;
            }

            [RpcContract]
            public interface ICalc : ICalc1, ICalc2
            {
            }
            """);
    }

    [Fact]
    public async Task Events()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
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
            [RpcContract]
            public interface IFoo
            {
            }
            """);
    }

    [Fact]
    public async Task Overloads()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public interface IFoo
            {
                Task SayHi();
                Task SayHi(string name);
                Task SayHi(string name, int age);
            }
            """);
    }

    [Fact]
    public async Task UnsupportedReturnType()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public interface IMyService
            {
                int Add(int a, int b); // StreamJsonRpc0001
            }
            """);
    }
}
