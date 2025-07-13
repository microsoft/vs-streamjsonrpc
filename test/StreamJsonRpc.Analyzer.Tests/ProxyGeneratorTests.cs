// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CSharpSourceGeneratorVerifier<StreamJsonRpc.Analyzers.ProxyGenerator>;

public class ProxyGeneratorTests
{
    [Fact]
    public async Task Public_NotNested()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IMyRpc
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
            internal partial class Wrapper
            {
                [RpcContract]
                public partial interface IMyRpc
                {
                    Task JustCancellationAsync(CancellationToken cancellationToken);
                }
            }
            """);
    }

    [Fact]
    public async Task NonPartialNestedInPartialType()
    {
        await VerifyCS.RunDefaultAsync("""
            internal partial class Wrapper
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
    public async Task PartialNestedInNonPartialType()
    {
        await VerifyCS.RunDefaultAsync("""
            internal class Wrapper
            {
                [RpcContract]
                public partial interface IMyRpc
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

            internal partial class Wrapper
            {
                [RpcContract]
                public partial interface IMyRpc
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
                public partial interface IMyRpc
                {
                    Task JustCancellationAsync(CancellationToken cancellationToken);
                }
            }

            namespace B
            {
                [RpcContract]
                public partial interface IMyRpc
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
                public partial interface IMyRpc
                {
                    Task JustCancellationAsync(CancellationToken cancellationToken);
                }
            }

            class B
            {
                [RpcContract]
                public partial interface IMyRpc
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
            public partial interface IFoo : IDisposable
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
            public partial interface IFoo
            {
                Task Dispose();
            }
            """);
    }

    [Fact]
    public async Task Interface_DerivesFromOthers()
    {
        await VerifyCS.RunDefaultAsync("""
            public partial interface IFoo
            {
                Task JustCancellationAsync(CancellationToken cancellationToken);
            }

            [RpcContract]
            public partial interface IFoo2 : IFoo
            {
                Task JustAnotherCancellationAsync(CancellationToken cancellationToken);
            }
            """);
    }

    [Fact]
    public async Task Interface_DerivesFromOthersWithRedundantMethods()
    {
        await VerifyCS.RunDefaultAsync("""
            public partial interface ICalc1
            {
                Task<int> AddAsync(int a, int b);
            }

            public partial interface ICalc2
            {
                Task<int> AddAsync(int a, int b);
            }

            [RpcContract]
            public partial interface ICalc : ICalc1, ICalc2
            {
            }
            """);
    }

    [Fact(Skip = "Does not yet work.")]
    public async Task Interface_DerivesFromOthersWithRedundantEvents()
    {
        await VerifyCS.RunDefaultAsync("""
            public partial interface ICalc1
            {
                event EventHandler Changed;
            }

            public partial interface ICalc2
            {
                event EventHandler Changed;
            }

            [RpcContract]
            public partial interface ICalc : ICalc1, ICalc2
            {
            }
            """);
    }

    [Fact]
    public async Task Events()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            partial interface IFoo
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
            public partial interface IFoo
            {
            }
            """);
    }

    [Fact]
    public async Task Overloads()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IFoo
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
            public partial interface IMyService
            {
                int Add(int a, int b); // StreamJsonRpc0011
            }
            """);
    }

    [Fact]
    public async Task NullableTypeArgument()
    {
        await VerifyCS.RunDefaultAsync("""
            #nullable enable

            [RpcContract]
            public partial interface IMyService
            {
                Task<object?> GetNullableIntAsync(string? value);
            }
            """);
    }

    [Fact]
    public async Task Interceptor_AttachOfTNoOptions()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
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
    public async Task Interceptor_AttachTwice()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IMyService
            {
            }

            [RpcContract]
            public partial interface IMyService2
            {
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    IMyService service = rpc.Attach<IMyService>();
                    IMyService2 service2 = rpc.Attach<IMyService2>();
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_AttachMultipleInterfaces_CollectionInitializer()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IMyService
            {
                Task Task1();
            }

            [RpcContract]
            public partial interface IMyService2
            {
                Task Task2();
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    object service = rpc.Attach([typeof(IMyService), typeof(IMyService2)], null);
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_AttachMultipleInterfaces_ArrayInitializer()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IMyService
            {
                Task Task1();
            }

            [RpcContract]
            public partial interface IMyService2
            {
                Task Task2();
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    object service = rpc.Attach(new Type[] { typeof(IMyService), typeof(IMyService2) }, null);
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_AttachMultipleInterfaces_OneDerivesFromTheOther()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IMyService
            {
                Task Task1(string name);
            }

            [RpcContract]
            public partial interface IMyService2 : IMyService
            {
                Task Task2(string color);
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    object service = rpc.Attach(new Type[] { typeof(IMyService), typeof(IMyService2) }, null);
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_AttachMultipleInterfaces_DistinctYetRedundantMethods()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IMyService
            {
                Task Task1(string name);
            }

            [RpcContract]
            public partial interface IMyService2
            {
                Task Task1(string name);
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    object service = rpc.Attach(new Type[] { typeof(IMyService), typeof(IMyService2) }, null);
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_AttachWithOptions()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IMyService
            {
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    IMyService service = rpc.Attach<IMyService>(new JsonRpcProxyOptions());
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_AttachType()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IMyService
            {
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    IMyService service = (IMyService)rpc.Attach(typeof(IMyService));
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_StaticStream()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IMyService
            {
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    IMyService service = JsonRpc.Attach<IMyService>(s);
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_StaticStreamStream()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IMyService
            {
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    IMyService service = JsonRpc.Attach<IMyService>(s, s);
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_StaticHandler()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IMyService
            {
            }

            class Test
            {
                void Foo(IJsonRpcMessageHandler h)
                {
                    IMyService service = JsonRpc.Attach<IMyService>(h);
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_StaticHandlerOptions()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IMyService
            {
            }

            class Test
            {
                void Foo(IJsonRpcMessageHandler h)
                {
                    IMyService service = JsonRpc.Attach<IMyService>(h, JsonRpcProxyOptions.Default);
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_AttachTypeWithOptions()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract]
            public partial interface IMyService
            {
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    JsonRpc rpc = new(s);
                    IMyService service = (IMyService)rpc.Attach(typeof(IMyService), new JsonRpcProxyOptions());
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_AllowAddingMembersLater_SameProject()
    {
        await VerifyCS.RunDefaultAsync("""
            [RpcContract, AllowAddingMembersLater]
            public partial interface IMyService
            {
            }

            class Test
            {
                void Foo(System.IO.Stream s)
                {
                    IMyService service = JsonRpc.Attach<IMyService>(s);
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_AllowAddingMembersLater_DifferentProject()
    {
        string libSource = VerifyCS.SourceFilePrefix + /* lang=c#-test */ """

            [RpcContract, AllowAddingMembersLater]
            public partial interface IMyService
            {
            }
            """;

        VerifyCS.Test test = new()
        {
            TestState =
            {
                Sources =
                {
                    VerifyCS.SourceFilePrefix + /* lang=c#-test */ """

                    class Test
                    {
                        void Foo(System.IO.Stream s)
                        {
                            IMyService service = JsonRpc.Attach<IMyService>(s);
                        }
                    }
                    """,
                },
                AdditionalProjects =
                {
                    ["ContractsLib"] =
                    {
                        Sources =
                        {
                            ("IMyService.cs", libSource),
                        },
                    },
                },
                AdditionalProjectReferences =
                {
                    "ContractsLib",
                },
            },
        };

        await test.RunAsync(TestContext.Current.CancellationToken);
    }
}
