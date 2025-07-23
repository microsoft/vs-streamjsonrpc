// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CSharpSourceGeneratorVerifier<StreamJsonRpc.Analyzers.ProxyGenerator>;

public class ProxyGeneratorTests
{
    [Fact]
    public async Task Public_NotNested()
    {
        await VerifyCS.RunDefaultAsync("""
            [JsonRpcContract]
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
                [JsonRpcContract]
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
                [JsonRpcContract]
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
                [JsonRpcContract]
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
                [JsonRpcContract]
                public partial interface IMyRpc
                {
                    Task JustCancellationAsync(CancellationToken cancellationToken);
                }
            }
            """);
    }

    [Fact]
    public async Task NestedAsProtected()
    {
        await VerifyCS.RunDefaultAsync("""
            namespace A;

            internal partial class Wrapper
            {
                [JsonRpcContract]
                protected partial interface IMyRpc
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
                [JsonRpcContract]
                public partial interface IMyRpc
                {
                    Task JustCancellationAsync(CancellationToken cancellationToken);
                }
            }

            namespace B
            {
                [JsonRpcContract]
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
                [JsonRpcContract]
                public partial interface IMyRpc
                {
                    Task JustCancellationAsync(CancellationToken cancellationToken);
                }
            }

            class B
            {
                [JsonRpcContract]
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
            [JsonRpcContract]
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
            [JsonRpcContract]
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

            [JsonRpcContract]
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

            [JsonRpcContract]
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

            [JsonRpcContract]
            public partial interface ICalc : ICalc1, ICalc2
            {
            }
            """);
    }

    [Fact]
    public async Task Events()
    {
        await VerifyCS.RunDefaultAsync("""
            [JsonRpcContract]
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
            [JsonRpcContract]
            public partial interface IFoo
            {
            }
            """);
    }

    [Fact]
    public async Task Overloads()
    {
        await VerifyCS.RunDefaultAsync("""
            [JsonRpcContract]
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
            [JsonRpcContract]
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

            [JsonRpcContract]
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
    public async Task Interceptor_AttachTwice()
    {
        await VerifyCS.RunDefaultAsync("""
            [JsonRpcContract]
            public partial interface IMyService
            {
            }

            [JsonRpcContract]
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
            [JsonRpcContract]
            public partial interface IMyService
            {
                Task Task1();
            }

            [JsonRpcContract]
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
            [JsonRpcContract]
            public partial interface IMyService
            {
                Task Task1();
            }

            [JsonRpcContract]
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
            [JsonRpcContract]
            public partial interface IMyService
            {
                Task Task1(string name);
            }

            [JsonRpcContract]
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
            [JsonRpcContract]
            public partial interface IMyService
            {
                Task Task1(string name);
            }

            [JsonRpcContract]
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
            [JsonRpcContract]
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
            [JsonRpcContract]
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
            [JsonRpcContract]
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
            [JsonRpcContract]
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
            [JsonRpcContract]
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
            [JsonRpcContract]
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
            [JsonRpcContract]
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
    public async Task Interceptor_UnknownTypes_Generic()
    {
        await VerifyCS.RunDefaultAsync("""
            class Test
            {
                void Foo<T>(System.IO.Stream s) where T: class
                {
                    JsonRpc rpc = new(s);
                    object service = rpc.Attach<T>();
                }
            }
            """);
    }

    [Fact]
    public async Task Interceptor_UnknownTypes_NonGeneric()
    {
        await VerifyCS.RunDefaultAsync("""
            class Test
            {
                void Foo(System.IO.Stream s, Type[] interfaces)
                {
                    JsonRpc rpc = new(s);
                    object service = rpc.Attach(interfaces, null);
                }
            }
            """);
    }

    [Fact]
    public async Task OneProxyPerInterfaceGroup_OneEmptyGroup()
    {
        await VerifyCS.RunDefaultAsync("""
            [JsonRpcContract]
            [JsonRpcProxyInterfaceGroup]
            [JsonRpcProxyInterfaceGroup(typeof(IMyService2))]
            [JsonRpcProxyInterfaceGroup(typeof(IMyService2), typeof(IMyService3))]
            partial interface IMyService
            {
            }

            partial interface IMyService2
            {
            }

            partial interface IMyService3
            {
            }
            """);
    }

    [Fact]
    public async Task OneProxyPerInterfaceGroup_NoEmptyGroup()
    {
        await VerifyCS.RunDefaultAsync("""
            [JsonRpcContract]
            [JsonRpcProxyInterfaceGroup(typeof(IMyService2))]
            [JsonRpcProxyInterfaceGroup(typeof(IMyService2), typeof(IMyService3))]
            partial interface IMyService
            {
            }

            partial interface IMyService2
            {
            }

            partial interface IMyService3
            {
            }
            """);
    }

    [Fact]
    public async Task Export_MixedInterfaceVisibility()
    {
        await VerifyCS.RunDefaultAsync("""
            [assembly: ExportRpcContractProxies]

            [JsonRpcContract]
            [JsonRpcProxyInterfaceGroup]
            [JsonRpcProxyInterfaceGroup(typeof(IInternalService))]
            public partial interface IPublicService
            {
            }

            [JsonRpcContract]
            internal partial interface IInternalService
            {
            }
            """);
    }

    [Fact]
    public async Task Interceptor_ForbidExternalProxies_SameProject()
    {
        await VerifyCS.RunDefaultAsync("""
            [assembly: ExportRpcContractProxies(ForbidExternalProxyGeneration = true)]

            [JsonRpcContract]
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
    public async Task Interceptor_ForbidExternalProxies_DifferentProject()
    {
        string libSource = VerifyCS.SourceFilePrefix + /* lang=c#-test */ """

            #nullable enable

            [assembly: ExportRpcContractProxies(ForbidExternalProxyGeneration = true)]

            [JsonRpcContract]
            [StreamJsonRpc.Reflection.JsonRpcProxyMapping(typeof(StreamJsonRpc.Generated.MyServiceProxy))] // sourcegen runs too late in the test harness, so we have to write it ourselves.
            public partial interface IMyService
            {
            }

            namespace StreamJsonRpc.Generated
            {
                public class MyServiceProxy(JsonRpc client, in StreamJsonRpc.Reflection.ProxyInputs inputs)
                    : StreamJsonRpc.Reflection.ProxyBase(client, inputs), IMyService
                {
                }
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
