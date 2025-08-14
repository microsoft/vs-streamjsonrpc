// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable CS0436 // Type conflicts with a type in the external assembly, but we want to test that we can handle this.

using System.Diagnostics;
using System.Reflection;
#if NET
using System.Runtime.Loader;
#endif
using Microsoft.VisualStudio.Threading;
using Nerdbank;
using StreamJsonRpc.Reflection;
using StreamJsonRpc.Tests;
using ExAssembly = StreamJsonRpc.Tests.ExternalAssembly;

public abstract partial class JsonRpcProxyGenerationTests : TestBase
{
    private readonly Server server;
    private FullDuplexStream serverStream;
    private JsonRpc serverRpc;

    private FullDuplexStream clientStream;
    private IServerDerived clientRpc;

    protected JsonRpcProxyGenerationTests(ITestOutputHelper logger, JsonRpcProxyOptions.ProxyImplementation proxyImplementation)
        : base(logger)
    {
        var streams = FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.DefaultProxyOptions = new(JsonRpcProxyOptions.Default) { ProxySource = proxyImplementation };
        this.clientRpc = this.AttachJsonRpc<IServerDerived>(this.clientStream);
        var clientJsonRpc = ((IJsonRpcClientProxy)this.clientRpc).JsonRpc;

        this.server = new Server();
        this.serverRpc = new JsonRpc(this.serverStream);
        this.serverRpc.AddLocalRpcTarget(this.server);
        this.serverRpc.StartListening();

        this.serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Verbose | SourceLevels.ActivityTracing);
        clientJsonRpc.TraceSource = new TraceSource("Client", SourceLevels.Verbose | SourceLevels.ActivityTracing);
        this.serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        clientJsonRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
    }

    [JsonRpcContract]
    public partial interface IServer
    {
        event EventHandler ItHappened;

        event EventHandler<CustomEventArgs> TreeGrown;

        event EventHandler<CustomNonDerivingEventArgs> AppleGrown;

        event EventHandler<bool> BoolEvent;

        Task<string> SayHiAsync();

        Task<string> SayHiAsync(string name);

        Task<int> AddAsync(int a, int b);

        Task IncrementAsync();

        ValueTask<bool> ManyParameters(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, CancellationToken cancellationToken);

        Task Dispose();
    }

    [JsonRpcContract]
    public partial interface IServerWithMoreEvents
    {
        event EventHandler AnotherEvent;
    }

    [JsonRpcContract]
    public partial interface IServerDerived : IServer
    {
        Task HeavyWorkAsync(CancellationToken cancellationToken);

        Task<int> HeavyWorkAsync(int param1, CancellationToken cancellationToken);

        [JsonRpcMethod("AnotherName")]
        Task<string> ARoseByAsync(string name);
    }

    ////[JsonRpcContract] Defining this attribute would produce a compile error, but we're testing runtime handling of the invalid case.
    public partial interface IServerWithBadCancellationParam
    {
        Task<int> HeavyWorkAsync(CancellationToken cancellationToken, int param1);
    }

    [JsonRpcContract]
    public partial interface IServer3
    {
        Task<string> SayHiAsync();

        [JsonRpcMethod("AnotherName")]
        Task<string> ARoseByAsync(string name);
    }

    [JsonRpcContract]
    public partial interface IServer2
    {
        Task<int> MultiplyAsync(int a, int b);
    }

    [JsonRpcContract]
    public partial interface IDisposableServer2 : IDisposable, IServer2
    {
    }

    [JsonRpcContract]
    public partial interface IServerWithParamsObject
    {
        Task<int> SumOfParameterObject(int a, int b);

        Task<int> SumOfParameterObject(int a, int b, CancellationToken cancellationToken);
    }

    [JsonRpcContract]
    public partial interface IServerWithParamsObjectNoResult
    {
        Task SumOfParameterObject(int a, int b);

        Task SumOfParameterObject(int a, int b, CancellationToken cancellationToken);
    }

    [JsonRpcContract]
    public partial interface IServerWithValueTasks
    {
        ValueTask DoSomethingValueAsync();

        ValueTask<int> AddValueAsync(int a, int b);
    }

    public interface IServerWithNonTaskReturnTypes
    {
        int Add(int a, int b);
    }

    [JsonRpcContract]
    public partial interface IServerWithVoidReturnType
    {
        void Notify(int a, int b);

        void NotifyWithCancellation(int a, int b, CancellationToken cancellationToken);
    }

    public interface IServerWithUnsupportedEventTypes
    {
        event Action MyActionEvent;
    }

    ////[JsonRpcContract] // This would trigger a compile error, but we're testing runtime handling of disallowed members.
    public interface IServerWithProperties
    {
        int Foo { get; set; }
    }

    ////[JsonRpcContract] // This would trigger a compile error, but we're testing runtime handling of disallowed members.
    public interface IServerWithGenericMethod
    {
        Task AddAsync<T>(T a, T b);
    }

    [JsonRpcContract]
    public partial interface IReferenceAnUnreachableAssembly
    {
        Task TakeAsync(UnreachableAssembly.SomeUnreachableClass obj);
    }

    [JsonRpcContract]
    internal partial interface IServerInternal :
        ExAssembly.ISomeInternalProxyInterface,
        IServerInternalWithInternalTypesFromOtherAssemblies,
        ExAssembly.IInternal.IPublicNestedInInternalInterface
    {
        Task<int> AddAsync(int a, int b);
    }

    [JsonRpcContract]
    internal partial interface IServerInternalWithInternalTypesFromOtherAssemblies
    {
        Task<ExAssembly.SomeOtherInternalType> SomeMethodAsync();
    }

    internal partial interface IRemoteService
    {
        [JsonRpcContract]
        internal partial interface ICallback : ExAssembly.IInternalGenericInterface<ExAssembly.SomeOtherInternalType?>
        {
        }
    }

    [JsonRpcContract]
    [JsonRpcProxyInterfaceGroup(typeof(IInterfaceGroup2))]
    internal partial interface IInterfaceGroup1;

    [JsonRpcContract]
    internal partial interface IInterfaceGroup2;

    [JsonRpcContract]
    internal partial interface ITimeTestedProxy
    {
        event EventHandler<CustomEventArgs> TestEvent;

        Task<string> WhatIsYourNameAsync(CancellationToken caancellationToken);

        Task HereIsMyCardAsync(string name);
    }

    protected JsonRpcProxyOptions DefaultProxyOptions { get; }

    [Fact]
    public void As()
    {
        var streams = FullDuplexStream.CreateStreams();
        var rpc = new JsonRpc(streams.Item1);
        Type oneStepRemoved = typeof(IInterfaceGroup1); // do this so the source generator cannot 'see' which interface(s) are required by the caller.
        IJsonRpcClientProxy clientRpc = (IJsonRpcClientProxy)rpc.Attach(oneStepRemoved, new JsonRpcProxyOptions(this.DefaultProxyOptions) { AcceptProxyWithExtraInterfaces = true });

        // Verify the test gets the multi-interface implementation we expect and require for this to be an effective test.
        Assert.IsAssignableFrom<IInterfaceGroup1>(clientRpc);
        Assert.IsAssignableFrom<IInterfaceGroup2>(clientRpc);

        // Now ask the proxy itself whether it was asked to implement both.
        Assert.NotNull(clientRpc.As<IInterfaceGroup1>());
        Assert.Null(clientRpc.As<IInterfaceGroup2>());
    }

    [Fact]
    public void InternalInterface_DerivingFromInternalInterfaceInOtherAssembly()
    {
        this.AttachJsonRpc<IRemoteService.ICallback>(new MemoryStream());
    }

    [Fact]
    public void Attach_NonGeneric()
    {
        var streams = FullDuplexStream.CreateStreams();
        var rpc = new JsonRpc(streams.Item1);
        var clientRpc = (IServerDerived)rpc.Attach(typeof(IServerDerived), this.DefaultProxyOptions);
        Assert.IsType(this.clientRpc.GetType(), clientRpc);
    }

    [Fact]
    public async Task Attach_MultipleInterfaces()
    {
        var streams = FullDuplexStream.CreateStreams();

        JsonRpc serverRpc = JsonRpc.Attach(streams.Item2, this.server);

        JsonRpc clientRpc = new JsonRpc(streams.Item1);
        object clientProxy = clientRpc.Attach([typeof(IServer), typeof(IServer2), typeof(IServer3), typeof(IServerWithMoreEvents)], this.DefaultProxyOptions);
        IServer client1 = Assert.IsAssignableFrom<IServer>(clientProxy);
        IServer2 client2 = Assert.IsAssignableFrom<IServer2>(clientProxy);
        IServer3 client3 = Assert.IsAssignableFrom<IServer3>(clientProxy);
        IServerWithMoreEvents client4 = Assert.IsAssignableFrom<IServerWithMoreEvents>(clientProxy);
        Assert.IsNotAssignableFrom<IServerDerived>(clientProxy);

        clientRpc.StartListening();
        Assert.Equal("Hi!", await client1.SayHiAsync().WithCancellation(this.TimeoutToken));
        Assert.Equal(6, await client2.MultiplyAsync(2, 3).WithCancellation(this.TimeoutToken));
        Assert.Equal("TEST", await client3.ARoseByAsync("test").WithCancellation(this.TimeoutToken));

        // Test events across multiple interfaces.
        AsyncManualResetEvent itHappenedCompletion = new(), anotherEventCompletion = new();
        client1.ItHappened += (s, e) => itHappenedCompletion.Set();
        client4.AnotherEvent += (s, e) => anotherEventCompletion.Set();

        this.server.OnItHappened(EventArgs.Empty);
        this.server.OnAnotherEvent(EventArgs.Empty);
        await itHappenedCompletion.WaitAsync(TestContext.Current.CancellationToken).WithCancellation(this.TimeoutToken);
        await anotherEventCompletion.WaitAsync(TestContext.Current.CancellationToken).WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public void Attach_MultipleInterfaces_TypeReuse()
    {
        var streams = FullDuplexStream.CreateStreams();
        var rpc = new JsonRpc(streams.Item1);
        object clientRpc12a = rpc.Attach([typeof(IServer), typeof(IServer2)], this.DefaultProxyOptions);

        streams = FullDuplexStream.CreateStreams();
        rpc = new JsonRpc(streams.Item1);
        object clientRpc12b = rpc.Attach([typeof(IServer), typeof(IServer2)], this.DefaultProxyOptions);
        Assert.Same(clientRpc12a.GetType(), clientRpc12b.GetType());
        Assert.IsAssignableFrom<IServer>(clientRpc12a);
        Assert.IsAssignableFrom<IServer2>(clientRpc12a);

        streams = FullDuplexStream.CreateStreams();
        rpc = new JsonRpc(streams.Item1);
        object clientRpc13 = rpc.Attach([typeof(IServer), typeof(IServer3)], this.DefaultProxyOptions);
        Assert.NotSame(clientRpc12a.GetType(), clientRpc13.GetType());
        Assert.IsAssignableFrom<IServer>(clientRpc13);
        Assert.IsAssignableFrom<IServer3>(clientRpc13);
        Assert.IsNotAssignableFrom<IServer2>(clientRpc13);
    }

    [Fact]
    public void Attach_NonUniqueList()
    {
        var streams = FullDuplexStream.CreateStreams();
        var rpc = new JsonRpc(streams.Item1);
        object proxy = rpc.Attach([typeof(IServer), typeof(IServer)], this.DefaultProxyOptions);
        Assert.IsAssignableFrom<IServer>(proxy);
    }

    [Fact]
    public void Attach_BaseAndDerivedTypes()
    {
        var streams = FullDuplexStream.CreateStreams();
        var rpc = new JsonRpc(streams.Item1);
        object proxy = rpc.Attach([typeof(IServer), typeof(IServerDerived)], this.DefaultProxyOptions);
        Assert.IsAssignableFrom<IServer>(proxy);
        Assert.IsAssignableFrom<IServerDerived>(proxy);
    }

    [Fact]
    public void ProxyTypeIsReused()
    {
        var streams = FullDuplexStream.CreateStreams();
        var clientRpc = this.AttachJsonRpc<IServerDerived>(streams.Item1);
        Assert.IsType(this.clientRpc.GetType(), clientRpc);
    }

    [Fact]
    public async Task CallMethod_String_String()
    {
        Assert.Equal("Hi, Andrew!", await this.clientRpc.SayHiAsync("Andrew"));
    }

    [Fact]
    public async Task RpcInterfaceCanDispose_IDisposable()
    {
        var streams = FullDuplexStream.CreateStreams();

        var clientRpc = this.AttachJsonRpc<IDisposableServer2>(streams.Item1);
        var server = new Server2();

        this.serverRpc = new JsonRpc(streams.Item2);
        this.serverRpc.AddLocalRpcTarget(server);
        this.serverRpc.StartListening();

        Assert.Equal(6, await clientRpc.MultiplyAsync(2, 3));
        clientRpc.Dispose();
        Assert.True(((IJsonRpcClientProxy)clientRpc).JsonRpc.IsDisposed);
    }

    [Fact]
    public async Task CallMethod_void_String()
    {
        Assert.Equal("Hi!", await this.clientRpc.SayHiAsync());
    }

    [Fact]
    public async Task CallMethod_IntInt_Int()
    {
        Assert.Equal(3, await this.clientRpc.AddAsync(1, 2));
    }

    [Fact]
    public async Task CallMethod_void_void()
    {
        await this.clientRpc.IncrementAsync();
        Assert.Equal(1, this.server.Counter);
    }

    [Theory]
    [CombinatorialData]
    public async Task CallVoidMethod(bool useNamedParameters)
    {
        var clientProxy = ((IJsonRpcClientProxy)this.clientRpc).JsonRpc.Attach<IServerWithVoidReturnType>(new JsonRpcProxyOptions { ServerRequiresNamedArguments = useNamedParameters });
        clientProxy.Notify(5, 8);
        Assert.Equal(13, await this.server.NotifyResult.Task.WithCancellation(this.TimeoutToken));
    }

    /// <summary>
    /// Verifies that void returning methods with CancellationToken 'work' (even if we don't honor the CancellationToken).
    /// </summary>
    [Theory]
    [CombinatorialData]
    public async Task CallVoidMethodWithCancellationToken(bool useNamedParameters)
    {
        var clientProxy = ((IJsonRpcClientProxy)this.clientRpc).JsonRpc.Attach<IServerWithVoidReturnType>(new JsonRpcProxyOptions { ServerRequiresNamedArguments = useNamedParameters });
        clientProxy.NotifyWithCancellation(5, 8, this.TimeoutToken);
        Assert.Equal(13, await this.server.NotifyResult.Task.WithCancellation(this.TimeoutToken));
    }

    [Fact]
    public async Task ImplementsIDisposableObservable()
    {
        var disposableClient = (IDisposableObservable)this.clientRpc;
        Assert.False(disposableClient.IsDisposed);
        disposableClient.Dispose();
        Assert.True(disposableClient.IsDisposed);

        // There is an async delay in disposal of the clientStream when pipes are involved.
        // Tolerate that while verifying that it does eventually close.
        while (!this.clientStream.IsDisposed)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
            this.TimeoutToken.ThrowIfCancellationRequested();
        }
    }

    [Fact]
    public void IsDisposedReturnsTrueWhenJsonRpcIsDisposed()
    {
        JsonRpc jsonRpc = ((IJsonRpcClientProxy)this.clientRpc).JsonRpc;
        jsonRpc.Dispose();

        var disposableClient = (IDisposableObservable)this.clientRpc;
        Assert.True(disposableClient.IsDisposed);
    }

    [Fact]
    public void DisposeTwiceDoesNotThrow()
    {
        var disposableClient = (IDisposable)this.clientRpc;
        disposableClient.Dispose();
        disposableClient.Dispose();
    }

    [Fact]
    public async Task TaskReturningRpcMethodsThrowAfterDisposal()
    {
        var disposableClient = (IDisposable)this.clientRpc;
        disposableClient.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => this.clientRpc.AddAsync(1, 2));
    }

    [Fact]
    public void VoidReturningRpcMethodsThrowAfterDisposal()
    {
        var clientProxy = ((IJsonRpcClientProxy)this.clientRpc).JsonRpc.Attach<IServerWithVoidReturnType>();
        var disposableClient = (IDisposable)clientProxy;
        disposableClient.Dispose();
        Assert.Throws<ObjectDisposedException>(() => clientProxy.Notify(1, 2));
    }

    [Fact]
    public async Task DisposeCollision()
    {
        // We're calling IServer.Dispose -- NOT IDisposable.Dispose here.
        // Verify that it invokes the server method rather than disposing of the client proxy.
        await this.clientRpc.Dispose();
        Assert.Equal(-1, this.server.Counter);
        Assert.False(this.clientStream.IsDisposed);
    }

    [Fact]
    [Trait("NegativeTest", "")]
    public void NonTaskReturningMethod()
    {
        var streams = FullDuplexStream.CreateStreams();
#pragma warning disable StreamJsonRpc0003 // Use JsonRpcContractAttribute
        var exception = Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<IServerWithNonTaskReturnTypes>(streams.Item1));
#pragma warning restore StreamJsonRpc0003 // Use JsonRpcContractAttribute
        this.Logger.WriteLine(exception.Message);
    }

    [Fact]
    [Trait("NegativeTest", "")]
    public void UnsupportedDelegateTypeOnEvent()
    {
#pragma warning disable StreamJsonRpc0003 // Use JsonRpcContractAttribute
        var exception = Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<IServerWithUnsupportedEventTypes>(this.clientStream));
#pragma warning restore StreamJsonRpc0003 // Use JsonRpcContractAttribute
        this.Logger.WriteLine(exception.Message);
    }

    [Fact]
    [Trait("NegativeTest", "")]
    public void PropertyOnInterface()
    {
#pragma warning disable StreamJsonRpc0003 // Use JsonRpcContractAttribute
        var exception = Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<IServerWithProperties>(this.clientStream));
#pragma warning restore StreamJsonRpc0003 // Use JsonRpcContractAttribute
        this.Logger.WriteLine(exception.Message);
    }

    [Fact]
    [Trait("NegativeTest", "")]
    public void GenericMethodOnInterface()
    {
#pragma warning disable StreamJsonRpc0003 // Use JsonRpcContractAttribute
        var exception = Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<IServerWithGenericMethod>(this.clientStream));
#pragma warning restore StreamJsonRpc0003 // Use JsonRpcContractAttribute
        this.Logger.WriteLine(exception.Message);
    }

    [Fact]
    [Trait("NegativeTest", "")]
    public void GenerateProxyFromClassNotSuppported()
    {
#pragma warning disable StreamJsonRpc0004 // Only interfaces allowed
        var exception = Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<EmptyClass>(this.clientStream));
#pragma warning restore StreamJsonRpc0004 // Only interfaces allowed
        this.Logger.WriteLine(exception.Message);
    }

    [Fact]
    [Trait("NegativeTest", "")]
    public void GenerateProxyFromClassNotSuppported_NotNestedClass()
    {
#pragma warning disable StreamJsonRpc0004 // Only interfaces allowed
        var exception = Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<JsonRpcProxyGenerationTests>(this.clientStream));
#pragma warning restore StreamJsonRpc0004 // Only interfaces allowed
        this.Logger.WriteLine(exception.Message);
    }

    [Fact]
    public async Task CallMethod_CancellationToken_int()
    {
        await this.clientRpc.HeavyWorkAsync(CancellationToken.None);
        Assert.Equal(1, this.server.Counter);
        this.server.MethodEntered.Reset();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.clientRpc.HeavyWorkAsync(new CancellationToken(canceled: true)));
        Assert.False(this.server.MethodEntered.IsSet);

        var cts = new CancellationTokenSource();
        this.server.ResumeMethod.Reset();
        Task task = this.clientRpc.HeavyWorkAsync(cts.Token);
        await this.server.MethodEntered.WaitAsync(TestContext.Current.CancellationToken).WithCancellation(this.TimeoutToken);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task CallMethod_intCancellationToken_int()
    {
        Assert.Equal(123, await this.clientRpc.HeavyWorkAsync(123, CancellationToken.None));
        this.server.MethodEntered.Reset();
        this.server.MethodResult = new TaskCompletionSource<int>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.clientRpc.HeavyWorkAsync(456, new CancellationToken(canceled: true)));
        Assert.False(this.server.MethodEntered.IsSet);

        var cts = new CancellationTokenSource();
        this.server.ResumeMethod.Reset();
        Task task = this.clientRpc.HeavyWorkAsync(456, cts.Token);
        await this.server.MethodEntered.WaitAsync(TestContext.Current.CancellationToken).WithCancellation(this.TimeoutToken);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(456, await this.server.MethodResult.Task); // assert that the argument we passed actually reached the method
    }

    [Fact]
    public void CancellationTokenInBadPositionIsRejected()
    {
#pragma warning disable StreamJsonRpc0003 // Use JsonRpcContractAttribute
        Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<IServerWithBadCancellationParam>(new MemoryStream()));
#pragma warning restore StreamJsonRpc0003 // Use JsonRpcContractAttribute
    }

    /// <summary>
    /// Verifies that object.ToString() does not get relayed to the service.
    /// </summary>
    [Fact]
    public void CallBaseMethods()
    {
        string? actual = this.clientRpc.ToString();
        this.Logger.WriteLine(actual ?? string.Empty);
        Assert.Equal(this.clientRpc.GetType().FullName, actual);
    }

    [Fact]
    public async Task AttachSecondProxy()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new Server();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var clientRpc = new JsonRpc(streams.Item1);
        var client1 = clientRpc.Attach<IServer>();
        var client2 = clientRpc.Attach<IServer2>();
        clientRpc.StartListening();

        Assert.Equal(3, await client1.AddAsync(1, 2));
        Assert.Equal(6, await client2.MultiplyAsync(2, 3));
    }

    [Fact]
    public void InstanceProxiesImplementIJsonRpcClientProxy()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new Server();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var clientRpc = new JsonRpc(streams.Item1);
        var client1 = clientRpc.Attach<IServer>();
        Assert.Same(clientRpc, ((IJsonRpcClientProxy)client1).JsonRpc);
    }

    [Fact]
    public async Task InternalInterface()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new ServerOfInternalInterface();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var clientRpc = JsonRpc.Attach(streams.Item1);

        // Try the first internal interface, which is external to this test assembly
        var proxy1 = clientRpc.Attach<ExAssembly.ISomeInternalProxyInterface>();
        Assert.Equal(-1, await proxy1.SubtractAsync(1, 2).WithCancellation(this.TimeoutToken));

        // Now create a proxy for another interface that is internal within this assembly, but derives from the external assembly's internal interface.
        // This verifies that we can handle multiple sets of assemblies which we need internal visibility into, as well as that it can track base type interfaces.
        var proxy2 = clientRpc.Attach<IServerInternal>();
        Assert.Equal(3, await proxy2.AddAsync(1, 2).WithCancellation(this.TimeoutToken));
    }

    [Fact]
    public async Task PublicInterfaceNestedInInternalInterface()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new ServerOfInternalInterface();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var clientRpc = JsonRpc.Attach(streams.Item1);

        // Try the first internal interface, which is external to this test assembly
        var proxy1 = clientRpc.Attach<ExAssembly.IInternal.IPublicNestedInInternalInterface>();
        Assert.Equal(-1, await proxy1.SubtractAsync(1, 2).WithCancellation(this.TimeoutToken));
    }

    [Fact]
    public async Task InternalInterface_WithInternalMembersFromOtherAssemblies()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new ServerOfInternalInterface();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var clientRpc = JsonRpc.Attach(streams.Item1);

        // Try the first internal interface, which is external to this test assembly
        var proxy1 = clientRpc.Attach<IServerInternalWithInternalTypesFromOtherAssemblies>();
        Assert.NotNull(await proxy1.SomeMethodAsync().WithCancellation(this.TimeoutToken));
    }

    [Fact]
    public async Task RPCMethodNameSubstitution()
    {
        Assert.Equal("ANDREW", await this.clientRpc.ARoseByAsync("andrew"));
    }

    [Fact]
    public async Task RPCMethodNameSubstitutionByOptions()
    {
        var streams = FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        var camelCaseOptions = new JsonRpcProxyOptions { MethodNameTransform = CommonMethodNameTransforms.CamelCase };
        var prefixOptions = new JsonRpcProxyOptions { MethodNameTransform = CommonMethodNameTransforms.Prepend("ns.") };

        // Construct two client proxies with conflicting method transforms to prove that each instance returned retains its unique options.
        var clientRpc = new JsonRpc(this.clientStream);
        var clientRpcWithCamelCase = clientRpc.Attach<IServer3>(camelCaseOptions);
        var clientRpcWithPrefix = clientRpc.Attach<IServer3>(prefixOptions);
        clientRpc.StartListening();

        // Construct the server to only respond to one set of method names for now to confirm that the client is sending the right one.
        this.serverRpc = new JsonRpc(this.serverStream, this.serverStream);
        this.serverRpc.AddLocalRpcTarget(this.server, new JsonRpcTargetOptions { MethodNameTransform = camelCaseOptions.MethodNameTransform });
        this.serverRpc.StartListening();

        Assert.Equal("Hi!", await clientRpcWithCamelCase.SayHiAsync()); // "sayHiAsync"
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => clientRpcWithPrefix.SayHiAsync()); // "ns.SayHiAsync"
        Assert.Equal("ANDREW", await clientRpcWithCamelCase.ARoseByAsync("andrew")); // "anotherName"
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => clientRpcWithPrefix.ARoseByAsync("andrew")); // "ns.AnotherName"

        // Prepare the server to *ALSO* accept method names with a prefix.
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(this.server, new JsonRpcTargetOptions { MethodNameTransform = prefixOptions.MethodNameTransform });

        // Retry with our second client proxy to send messages which the server should now accept.
        Assert.Equal("Hi!", await clientRpcWithPrefix.SayHiAsync()); // "ns.SayHiAsync"
        Assert.Equal("ANDREW", await clientRpcWithPrefix.ARoseByAsync("andrew")); // "ns.AnotherName"
    }

    [Fact]
    public async Task GenericEventRaisedOnClient()
    {
        var tcs = new TaskCompletionSource<CustomEventArgs>();
        EventHandler<CustomEventArgs> handler = (sender, args) => tcs.SetResult(args);
        this.clientRpc.TreeGrown += handler;
        var expectedArgs = new CustomEventArgs { Seeds = 5 };
        this.server.OnTreeGrown(expectedArgs);
        var actualArgs = await tcs.Task.WithCancellation(this.TimeoutToken);
        Assert.Equal(expectedArgs.Seeds, actualArgs.Seeds);

        // Now unregister and confirm we don't get notified.
        this.clientRpc.TreeGrown -= handler;
        tcs = new TaskCompletionSource<CustomEventArgs>();
        this.server.OnTreeGrown(expectedArgs);
        await Assert.ThrowsAsync<TimeoutException>(() => tcs.Task.WithTimeout(ExpectedTimeout));
        Assert.False(tcs.Task.IsCompleted);
    }

    [Fact]
    public async Task GenericEventWithoutEventArgsBaseTypeRaisedOnClient()
    {
        var tcs = new TaskCompletionSource<CustomNonDerivingEventArgs>();
        EventHandler<CustomNonDerivingEventArgs> handler = (sender, args) => tcs.SetResult(args);
        this.clientRpc.AppleGrown += handler;
        var expectedArgs = new CustomNonDerivingEventArgs { Color = "Red" };
        this.server.OnAppleGrown(expectedArgs);
        var actualArgs = await tcs.Task.WithCancellation(this.TimeoutToken);
        Assert.Equal(expectedArgs.Color, actualArgs.Color);

        // Now unregister and confirm we don't get notified.
        this.clientRpc.AppleGrown -= handler;
        tcs = new TaskCompletionSource<CustomNonDerivingEventArgs>();
        this.server.OnAppleGrown(expectedArgs);
        await Assert.ThrowsAsync<TimeoutException>(() => tcs.Task.WithTimeout(ExpectedTimeout));
        Assert.False(tcs.Task.IsCompleted);
    }

    [Fact]
    public async Task GenericEventWithBoolArgRaisedOnClient()
    {
        var tcs = new TaskCompletionSource<bool>();
        EventHandler<bool> handler = (sender, args) => tcs.SetResult(args);
        this.clientRpc.BoolEvent += handler;
        var expectedArgs = true;
        this.server.OnBoolEvent(expectedArgs);
        var actualArgs = await tcs.Task.WithCancellation(this.TimeoutToken);
        Assert.Equal(expectedArgs, actualArgs);

        // Now unregister and confirm we don't get notified.
        this.clientRpc.BoolEvent -= handler;
        tcs = new TaskCompletionSource<bool>();
        this.server.OnBoolEvent(expectedArgs);
        await Assert.ThrowsAsync<TimeoutException>(() => tcs.Task.WithTimeout(ExpectedTimeout));
        Assert.False(tcs.Task.IsCompleted);
    }

    [Fact]
    public async Task NonGenericEventRaisedOnClient()
    {
        var tcs = new TaskCompletionSource<EventArgs>();
        EventHandler handler = (sender, args) => tcs.SetResult(args);
        this.clientRpc.ItHappened += handler;
        this.server.OnItHappened(EventArgs.Empty);
        var actualArgs = await tcs.Task.WithCancellation(this.TimeoutToken);
        Assert.NotNull(actualArgs);

        // Now unregister and confirm we don't get notified.
        this.clientRpc.ItHappened -= handler;
        tcs = new TaskCompletionSource<EventArgs>();
        this.server.OnItHappened(EventArgs.Empty);
        await Assert.ThrowsAsync<TimeoutException>(() => tcs.Task.WithTimeout(ExpectedTimeout));
        Assert.False(tcs.Task.IsCompleted);
    }

    [Fact]
    public async Task NamingTransformsAreAppliedToEvents()
    {
        var streams = FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        var camelCaseOptions = new JsonRpcProxyOptions { EventNameTransform = CommonMethodNameTransforms.CamelCase };
        var prefixOptions = new JsonRpcProxyOptions { EventNameTransform = CommonMethodNameTransforms.Prepend("ns.") };

        // Construct two client proxies with conflicting method transforms to prove that each instance returned retains its unique options.
        var clientRpc = new JsonRpc(this.clientStream);
        var clientRpcWithCamelCase = clientRpc.Attach<IServer>(camelCaseOptions);
        var clientRpcWithPrefix = clientRpc.Attach<IServer>(prefixOptions);
        clientRpc.StartListening();

        // Construct the server to only respond to one set of method names for now to confirm that the client is sending the right one.
        this.serverRpc = new JsonRpc(this.serverStream, this.serverStream);
        this.serverRpc.AddLocalRpcTarget(this.server, new JsonRpcTargetOptions { EventNameTransform = camelCaseOptions.EventNameTransform });
        this.serverRpc.StartListening();

        var tcs = new TaskCompletionSource<EventArgs>();
        EventHandler handler = (sender, args) => tcs.SetResult(args);
        clientRpcWithCamelCase.ItHappened += handler;
        this.server.OnItHappened(EventArgs.Empty);
        var actualArgs = await tcs.Task.WithCancellation(this.TimeoutToken);
        Assert.NotNull(actualArgs);

        clientRpcWithCamelCase.ItHappened -= handler;
        clientRpcWithPrefix.ItHappened += handler;
        tcs = new TaskCompletionSource<EventArgs>();
        this.server.OnItHappened(EventArgs.Empty);
        await Assert.ThrowsAsync<TimeoutException>(() => tcs.Task.WithTimeout(ExpectedTimeout));
        Assert.False(tcs.Task.IsCompleted);

        clientRpcWithPrefix.ItHappened -= handler;
    }

    [Fact]
    public async Task CallServerWithParameterObject()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new Server();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var client = new JsonRpc(streams.Item1);
        var clientRpc = client.Attach<IServerWithParamsObject>(new JsonRpcProxyOptions { ServerRequiresNamedArguments = true });
        client.StartListening();

        int result = await clientRpc.SumOfParameterObject(1, 2, this.TimeoutToken);
        Assert.Equal(3, result);
    }

    [Fact]
    public async Task CallServerWithParameterObject_WithCancellationToken()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new Server();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var client = new JsonRpc(streams.Item1);
        var clientRpc = client.Attach<IServerWithParamsObject>(new JsonRpcProxyOptions { ServerRequiresNamedArguments = true });
        client.StartListening();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
        server.ResumeMethod.Reset();
        Task<int> task = clientRpc.SumOfParameterObject(1, 2, cts.Token);
        Assert.Equal(3, await server.MethodResult.Task);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task CallServerWithParameterObject_NoReturnValue()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new Server();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var client = new JsonRpc(streams.Item1);
        var clientRpc = client.Attach<IServerWithParamsObjectNoResult>(new JsonRpcProxyOptions { ServerRequiresNamedArguments = true });
        client.StartListening();

        await clientRpc.SumOfParameterObject(1, 2, this.TimeoutToken);
        int result = await server.MethodResult.Task;
        Assert.Equal(3, result);
    }

    [Fact]
    public async Task CallServerWithParameterObject_NoReturnValue_WithCancellationToken()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new Server();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var client = new JsonRpc(streams.Item1);
        var clientRpc = client.Attach<IServerWithParamsObjectNoResult>(new JsonRpcProxyOptions { ServerRequiresNamedArguments = true });
        client.StartListening();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
        server.ResumeMethod.Reset();
        Task task = clientRpc.SumOfParameterObject(1, 2, cts.Token);
        Assert.Equal(3, await server.MethodResult.Task);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task ValueTaskOfTReturningMethod()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new Server();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var clientRpc = this.AttachJsonRpc<IServerWithValueTasks>(streams.Item1);
        int sum = await clientRpc.AddValueAsync(1, 2);
        Assert.Equal(3, sum);
    }

    [Fact]
    public async Task ValueTaskReturningMethod()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new Server();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var clientRpc = this.AttachJsonRpc<IServerWithValueTasks>(streams.Item1);
        await clientRpc.DoSomethingValueAsync();
    }

    /// <summary>
    /// Validates that similar proxies are generated in the same dynamic assembly.
    /// </summary>
    [Fact]
    public void ReuseDynamicAssembliesTest()
    {
        JsonRpc clientRpc = new(Stream.Null);
        IServer proxy1 = clientRpc.Attach<IServer>(this.DefaultProxyOptions);
        IServer2 proxy2 = clientRpc.Attach<IServer2>(this.DefaultProxyOptions);
        Assert.Same(proxy1.GetType().Assembly, proxy2.GetType().Assembly);
    }

#if NET
    [Fact]
    public void DynamicAssembliesKeyedByAssemblyLoadContext()
    {
        UnreachableAssemblyTools.VerifyUnreachableAssembly();

        // Set up a new ALC that can find the hidden assembly, and ask for the proxy type.
        AssemblyLoadContext alc = UnreachableAssemblyTools.CreateContextForReachingTheUnreachable();

        JsonRpc clientRpc = new(Stream.Null);

        // Ensure we first generate a proxy in our own default ALC.
        // The goal being to emit a DynamicAssembly that we *might* reuse
        // for the later proxy for which the first DynamicAssembly is not appropriate.
        clientRpc.Attach<IServer>(this.DefaultProxyOptions);

        // Now take very specific steps to invoke the rest of the test in the other AssemblyLoadContext.
        // This is important so that our IReferenceAnUnreachableAssembly type will be able to resolve its
        // own type references to UnreachableAssembly.dll, which our own default ALC cannot do.
        MethodInfo helperMethodInfo = typeof(JsonRpcProxyGenerationTests).GetMethod(nameof(DynamicAssembliesKeyedByAssemblyLoadContext_Helper), BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo helperWithinAlc = UnreachableAssemblyTools.LoadHelperInAlc(alc, helperMethodInfo);
        helperWithinAlc.Invoke(null, [this.DefaultProxyOptions]);
    }

#endif

    protected T AttachJsonRpc<T>(Stream stream)
        where T : class
    {
        var rpc = new JsonRpc(stream);
        T proxy = rpc.Attach<T>(this.DefaultProxyOptions);
        rpc.StartListening();
        return proxy;
    }

#if NET

    private static void DynamicAssembliesKeyedByAssemblyLoadContext_Helper(JsonRpcProxyOptions options)
    {
        // Although this method executes within the special ALC,
        // StreamJsonRpc is loaded in the default ALC.
        // Therefore unless StreamJsonRpc is taking care to use a DynamicAssembly
        // that belongs to *this* ALC, it won't be able to resolve the same type references
        // that we can here (the ones from UnreachableAssembly).
        // That's what makes this test effective: it'll fail if the DynamicAssembly is shared across ALCs,
        // thereby verifying that StreamJsonRpc has a dedicated set of DynamicAssemblies for each ALC.
        JsonRpc clientRpc = new(Stream.Null);
        clientRpc.Attach<IReferenceAnUnreachableAssembly>(options);
    }

#endif

#if NO_INTERCEPTORS
    public class Dynamic(ITestOutputHelper logger) : JsonRpcProxyGenerationTests(logger, JsonRpcProxyOptions.ProxyImplementation.AlwaysDynamic);
#else
    public class SourceGenerated(ITestOutputHelper logger) : JsonRpcProxyGenerationTests(logger, JsonRpcProxyOptions.ProxyImplementation.AlwaysSourceGenerated)
    {
        /// <summary>
        /// The interceptor cannot fallback to dynamic proxies at runtime when the Options demands it,
        /// because doing so would generate linker warnings for NativeAOT apps.
        /// So if the calling code has the interceptor enabled, and demands dynamic proxies, we have no choice but to fail.
        /// </summary>
        [Fact]
        public void Interceptor_RejectsDynamicDemandAtRuntime()
        {
            JsonRpcProxyOptions options = new() { ProxySource = JsonRpcProxyOptions.ProxyImplementation.AlwaysDynamic };
            var streams = FullDuplexStream.CreateStreams();
            var rpc = new JsonRpc(streams.Item1);
            var ex = Assert.Throws<NotSupportedException>(() => rpc.Attach<IServer>(options));
            this.Logger.WriteLine(ex.Message);
        }

        [Fact]
        public void Interceptor_CanFindProxiesAtRuntime()
        {
            var streams = FullDuplexStream.CreateStreams();
            var rpc = new JsonRpc(streams.Item1);
            Type proxyType = typeof(IServer);
            IServer server = (IServer)rpc.Attach(proxyType, this.DefaultProxyOptions);
        }

        [Fact]
        public async Task CheckedInProxiesFromPastGenerationsStillWork()
        {
            // Test each proxy associatd with the time tested proxy interface.
            int failures = 0;
            foreach (JsonRpcProxyMappingAttribute mapping in typeof(ITimeTestedProxy).GetCustomAttributes<JsonRpcProxyMappingAttribute>())
            {
                try
                {
                    var streams = FullDuplexStream.CreateStreams();
                    using var clientRpc = new JsonRpc(streams.Item1);
                    TimeTestedServer server = new();
                    using var serverRpc = JsonRpc.Attach(streams.Item2, server);
                    var proxy = (ITimeTestedProxy)Activator.CreateInstance(mapping.ProxyClass, clientRpc, new ProxyInputs { ContractInterface = typeof(ITimeTestedProxy) })!;
                    clientRpc.StartListening();

                    // Test passing data in.
                    await proxy.HereIsMyCardAsync("My card");
                    Assert.Equal("My card", server.Card);

                    // Test getting data out.
                    Assert.Equal("Andrew", await proxy.WhatIsYourNameAsync(this.TimeoutToken));

                    // Test event handling.
                    TaskCompletionSource<CustomEventArgs> tcs = new();
                    proxy.TestEvent += (s, e) => tcs.TrySetResult(e);
                    var args = new CustomEventArgs { Seeds = 42 };
                    server.RaiseTestEvent(args);
                    Assert.Equal(args, await tcs.Task.WithCancellation(this.TimeoutToken));

                    this.Logger.WriteLine($"✅ {mapping.ProxyClass.Name}");
                }
                catch (Exception ex)
                {
                    this.Logger.WriteLine($"❌ {mapping.ProxyClass.Name}");
                    this.Logger.WriteLine($"  {ex.Message}");
                    failures++;
                }
            }

            Assert.Equal(0, failures);
        }
    }

#endif

    public class EmptyClass
    {
    }

    public class CustomEventArgs : EventArgs, IEquatable<CustomEventArgs>
    {
        public int Seeds { get; set; }

        public bool Equals(CustomEventArgs? other) => this.Seeds == other?.Seeds;
    }

    /// <summary>
    /// This class serves as a type argument to <see cref="EventHandler{TEventArgs}"/>
    /// but intentionally does *not* derive from <see cref="EventArgs"/>
    /// since that is no longer a requirement as of .NET 4.5.
    /// </summary>
    public class CustomNonDerivingEventArgs
    {
        public string? Color { get; set; }
    }

    internal class Server : IServerDerived, IServer2, IServer3, IServerWithValueTasks, IServerWithVoidReturnType, IServerWithMoreEvents
    {
        public event EventHandler? ItHappened;

        public event EventHandler<CustomEventArgs>? TreeGrown;

        public event EventHandler<CustomNonDerivingEventArgs>? AppleGrown;

        public event EventHandler<bool>? BoolEvent;

        public event EventHandler? AnotherEvent;

        public AsyncManualResetEvent MethodEntered { get; } = new AsyncManualResetEvent();

        public AsyncManualResetEvent ResumeMethod { get; } = new AsyncManualResetEvent(initialState: true);

        public TaskCompletionSource<int> MethodResult { get; set; } = new TaskCompletionSource<int>();

        public TaskCompletionSource<int> NotifyResult { get; set; } = new TaskCompletionSource<int>();

        public int Counter { get; set; }

        public Task<string> SayHiAsync() => Task.FromResult("Hi!");

        public Task<string> SayHiAsync(string name) => Task.FromResult($"Hi, {name}!");

        public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);

        public Task IncrementAsync()
        {
            this.Counter++;
            return Task.CompletedTask;
        }

        public Task Dispose()
        {
            this.Counter--;
            return Task.CompletedTask;
        }

        public async Task HeavyWorkAsync(CancellationToken cancellationToken)
        {
            this.MethodEntered.Set();
            await this.ResumeMethod.WaitAsync(cancellationToken);
            this.Counter++;
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task<int> HeavyWorkAsync(int param1, CancellationToken cancellationToken)
        {
            this.MethodEntered.Set();
            this.MethodResult.SetResult(param1);
            await this.ResumeMethod.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return param1;
        }

        public Task<int> MultiplyAsync(int a, int b) => Task.FromResult(a * b);

        public Task<string> ARoseByAsync(string name) => Task.FromResult(name.ToUpperInvariant());

        public async Task<int> SumOfParameterObject(Newtonsoft.Json.Linq.JToken paramObject, CancellationToken cancellationToken)
        {
            this.MethodEntered.Set();
            int sum = paramObject.Value<int>("a") + paramObject.Value<int>("b");
            this.MethodResult.SetResult(sum);
            await this.ResumeMethod.WaitAsync(cancellationToken);
            return sum;
        }

        public ValueTask DoSomethingValueAsync() => default;

        public ValueTask<int> AddValueAsync(int a, int b) => new ValueTask<int>(a + b);

        public void Notify(int a, int b) => this.NotifyResult.SetResult(a + b);

        public void NotifyWithCancellation(int a, int b, CancellationToken cancellationToken) => this.Notify(a, b);

        public ValueTask<bool> ManyParameters(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8, int p9, int p10, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        internal void OnItHappened(EventArgs args) => this.ItHappened?.Invoke(this, args);

        internal void OnTreeGrown(CustomEventArgs args) => this.TreeGrown?.Invoke(this, args);

        internal void OnAppleGrown(CustomNonDerivingEventArgs args) => this.AppleGrown?.Invoke(this, args);

        internal void OnBoolEvent(bool args) => this.BoolEvent?.Invoke(this, args);

        internal void OnAnotherEvent(EventArgs args) => this.AnotherEvent?.Invoke(this, args);
    }

    internal class Server2 : IServer2
    {
        public Task<int> MultiplyAsync(int a, int b) => Task.FromResult(a * b);
    }

    internal class ServerOfInternalInterface : IServerInternal
    {
        public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);

        public Task<ExAssembly.SomeOtherInternalType> SomeMethodAsync()
        {
            return Task.FromResult(new ExAssembly.SomeOtherInternalType());
        }

        public Task<int> SubtractAsync(int a, int b) => Task.FromResult(a - b);
    }

    internal class Callback : IRemoteService.ICallback
    {
        public Task<ExAssembly.SomeOtherInternalType?> GetOptionsAsync(ExAssembly.InternalStruct id, CancellationToken cancellationToken)
            => Task.FromResult<ExAssembly.SomeOtherInternalType?>(null);
    }

    private class TimeTestedServer : ITimeTestedProxy
    {
        public event EventHandler<CustomEventArgs>? TestEvent;

        internal string? Card { get; set; }

        public Task HereIsMyCardAsync(string name)
        {
            this.Card = name;
            return Task.CompletedTask;
        }

        public Task<string> WhatIsYourNameAsync(CancellationToken caancellationToken) => Task.FromResult("Andrew");

        public void RaiseTestEvent(CustomEventArgs args) => this.TestEvent?.Invoke(this, args);
    }
}
