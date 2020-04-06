// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Nerdbank;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcProxyGenerationTests : TestBase
{
    private readonly Server server;
    private FullDuplexStream serverStream;
    private JsonRpc serverRpc;

    private FullDuplexStream clientStream;
    private IServerDerived clientRpc;

    public JsonRpcProxyGenerationTests(ITestOutputHelper logger)
        : base(logger)
    {
        var streams = FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.clientRpc = JsonRpc.Attach<IServerDerived>(this.clientStream);

        this.server = new Server();
        this.serverRpc = JsonRpc.Attach(this.serverStream, this.server);
    }

    public interface IServer
    {
        event EventHandler ItHappened;

        event EventHandler<CustomEventArgs> TreeGrown;

        event EventHandler<CustomNonDerivingEventArgs> AppleGrown;

        event EventHandler<bool> BoolEvent;

        Task<string> SayHiAsync();

        Task<string> SayHiAsync(string name);

        Task<int> AddAsync(int a, int b);

        Task IncrementAsync();

        Task Dispose();
    }

    public interface IServerDerived : IServer
    {
        Task HeavyWorkAsync(CancellationToken cancellationToken);

        Task<int> HeavyWorkAsync(int param1, CancellationToken cancellationToken);

        [JsonRpcMethod("AnotherName")]
        Task<string> ARoseByAsync(string name);
    }

    public interface IServerWithBadCancellationParam
    {
        Task<int> HeavyWorkAsync(CancellationToken cancellationToken, int param1);
    }

    public interface IServer3
    {
        Task<string> SayHiAsync();

        [JsonRpcMethod("AnotherName")]
        Task<string> ARoseByAsync(string name);
    }

    public interface IServer2
    {
        Task<int> MultiplyAsync(int a, int b);
    }

    public interface IDisposableServer2 : IDisposable, IServer2
    {
    }

    public interface IServerWithParamsObject
    {
        Task<int> SumOfParameterObject(int a, int b);

        Task<int> SumOfParameterObject(int a, int b, CancellationToken cancellationToken);
    }

    public interface IServerWithParamsObjectNoResult
    {
        Task SumOfParameterObject(int a, int b);

        Task SumOfParameterObject(int a, int b, CancellationToken cancellationToken);
    }

    public interface IServerWithValueTasks
    {
        ValueTask DoSomethingValueAsync();

        ValueTask<int> AddValueAsync(int a, int b);
    }

    public interface IServerWithNonTaskReturnTypes
    {
        int Add(int a, int b);
    }

    public interface IServerWithUnsupportedEventTypes
    {
        event Action MyActionEvent;
    }

    public interface IServerWithProperties
    {
        int Foo { get; set; }
    }

    public interface IServerWithGenericMethod
    {
        Task AddAsync<T>(T a, T b);
    }

    internal interface IServerInternal : StreamJsonRpc.Tests.ExternalAssembly.ISomeInternalProxyInterface, IServerInternalWithInternalTypesFromOtherAssemblies
    {
        Task<int> AddAsync(int a, int b);
    }

    internal interface IServerInternalWithInternalTypesFromOtherAssemblies
    {
        Task<StreamJsonRpc.Tests.ExternalAssembly.SomeOtherInternalType> SomeMethodAsync();
    }

    [Fact]
    public void Attach_NonGeneric()
    {
        var streams = FullDuplexStream.CreateStreams();
        var rpc = new JsonRpc(streams.Item1);
        var clientRpc = (IServerDerived)rpc.Attach(typeof(IServerDerived));
        Assert.IsType(this.clientRpc.GetType(), clientRpc);
    }

    [Fact]
    public void ProxyTypeIsReused()
    {
        var streams = FullDuplexStream.CreateStreams();
        var clientRpc = JsonRpc.Attach<IServerDerived>(streams.Item1);
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

        var clientRpc = JsonRpc.Attach<IDisposableServer2>(streams.Item1);
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

    [Fact]
    public async Task ImplementsIDisposable()
    {
        var disposableClient = (IDisposable)this.clientRpc;
        disposableClient.Dispose();

        // There is an async delay in disposal of the clientStream when pipes are involved.
        // Tolerate that while verifying that it does eventually close.
        while (!this.clientStream.IsDisposed)
        {
            await Task.Delay(1);
            this.TimeoutToken.ThrowIfCancellationRequested();
        }
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
        var exception = Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<IServerWithNonTaskReturnTypes>(streams.Item1));
        this.Logger.WriteLine(exception.Message);
    }

    [Fact]
    [Trait("NegativeTest", "")]
    public void UnsupportedDelegateTypeOnEvent()
    {
        var exception = Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<IServerWithUnsupportedEventTypes>(this.clientStream));
        this.Logger.WriteLine(exception.Message);
    }

    [Fact]
    [Trait("NegativeTest", "")]
    public void PropertyOnInterface()
    {
        var exception = Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<IServerWithProperties>(this.clientStream));
        this.Logger.WriteLine(exception.Message);
    }

    [Fact]
    [Trait("NegativeTest", "")]
    public void GenericMethodOnInterface()
    {
        var exception = Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<IServerWithGenericMethod>(this.clientStream));
        this.Logger.WriteLine(exception.Message);
    }

    [Fact]
    [Trait("NegativeTest", "")]
    public void GenerateProxyFromClassNotSuppported()
    {
        var exception = Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<EmptyClass>(this.clientStream));
        this.Logger.WriteLine(exception.Message);
    }

    [Fact]
    [Trait("NegativeTest", "")]
    public void GenerateProxyFromClassNotSuppported_NotNestedClass()
    {
        var exception = Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<JsonRpcProxyGenerationTests>(this.clientStream));
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
        await this.server.MethodEntered.WaitAsync().WithCancellation(this.TimeoutToken);
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
        await this.server.MethodEntered.WaitAsync().WithCancellation(this.TimeoutToken);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(456, await this.server.MethodResult.Task); // assert that the argument we passed actually reached the method
    }

    [Fact]
    public void CancellationTokenInBadPositionIsRejected()
    {
        Assert.Throws<NotSupportedException>(() => JsonRpc.Attach<IServerWithBadCancellationParam>(new MemoryStream()));
    }

    /// <summary>
    /// Verifies that object.ToString() does not get relayed to the service.
    /// </summary>
    [Fact]
    public void CallBaseMethods()
    {
        Assert.Contains("proxy", this.clientRpc.ToString());
    }

    [Fact]
    public async Task AttachSecondProxy()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new Server();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var clientRpc = new JsonRpc(streams.Item1, streams.Item1);
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

        var clientRpc = new JsonRpc(streams.Item1, streams.Item1);
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
        var proxy1 = clientRpc.Attach<StreamJsonRpc.Tests.ExternalAssembly.ISomeInternalProxyInterface>();
        Assert.Equal(-1, await proxy1.SubtractAsync(1, 2).WithCancellation(this.TimeoutToken));

        // Now create a proxy for another interface that is internal within this assembly, but derives from the external assembly's internal interface.
        // This verifies that we can handle multiple sets of assemblies which we need internal visibility into, as well as that it can track base type interfaces.
        var proxy2 = clientRpc.Attach<IServerInternal>();
        Assert.Equal(3, await proxy2.AddAsync(1, 2).WithCancellation(this.TimeoutToken));
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
        var clientRpc = new JsonRpc(this.clientStream, this.clientStream);
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
        var clientRpc = new JsonRpc(this.clientStream, this.clientStream);
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

        var client = new JsonRpc(streams.Item1, streams.Item1);
        var clientRpc = client.Attach<IServerWithParamsObject>(new JsonRpcProxyOptions { ServerRequiresNamedArguments = true });
        client.StartListening();

        int result = await clientRpc.SumOfParameterObject(1, 2);
        Assert.Equal(3, result);
    }

    [Fact]
    public async Task CallServerWithParameterObject_WithCancellationToken()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new Server();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var client = new JsonRpc(streams.Item1, streams.Item1);
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

        var client = new JsonRpc(streams.Item1, streams.Item1);
        var clientRpc = client.Attach<IServerWithParamsObjectNoResult>(new JsonRpcProxyOptions { ServerRequiresNamedArguments = true });
        client.StartListening();

        await clientRpc.SumOfParameterObject(1, 2);
        int result = await server.MethodResult.Task;
        Assert.Equal(3, result);
    }

    [Fact]
    public async Task CallServerWithParameterObject_NoReturnValue_WithCancellationToken()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new Server();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var client = new JsonRpc(streams.Item1, streams.Item1);
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

        var clientRpc = JsonRpc.Attach<IServerWithValueTasks>(streams.Item1);
        int sum = await clientRpc.AddValueAsync(1, 2);
        Assert.Equal(3, sum);
    }

    [Fact]
    public async Task ValueTaskReturningMethod()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new Server();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var clientRpc = JsonRpc.Attach<IServerWithValueTasks>(streams.Item1);
        await clientRpc.DoSomethingValueAsync();
    }

    public class EmptyClass
    {
    }

    public class CustomEventArgs : EventArgs
    {
        public int Seeds { get; set; }
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

    internal class Server : IServerDerived, IServer2, IServer3, IServerWithValueTasks
    {
        public event EventHandler? ItHappened;

        public event EventHandler<CustomEventArgs>? TreeGrown;

        public event EventHandler<CustomNonDerivingEventArgs>? AppleGrown;

        public event EventHandler<bool>? BoolEvent;

        public AsyncManualResetEvent MethodEntered { get; } = new AsyncManualResetEvent();

        public AsyncManualResetEvent ResumeMethod { get; } = new AsyncManualResetEvent(initialState: true);

        public TaskCompletionSource<int> MethodResult { get; set; } = new TaskCompletionSource<int>();

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
            await this.ResumeMethod.WaitAsync().WithCancellation(cancellationToken);
            this.Counter++;
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task<int> HeavyWorkAsync(int param1, CancellationToken cancellationToken)
        {
            this.MethodEntered.Set();
            this.MethodResult.SetResult(param1);
            await this.ResumeMethod.WaitAsync().WithCancellation(cancellationToken);
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
            await this.ResumeMethod.WaitAsync().WithCancellation(cancellationToken);
            return sum;
        }

        public ValueTask DoSomethingValueAsync() => default;

        public ValueTask<int> AddValueAsync(int a, int b) => new ValueTask<int>(a + b);

        internal void OnItHappened(EventArgs args) => this.ItHappened?.Invoke(this, args);

        internal void OnTreeGrown(CustomEventArgs args) => this.TreeGrown?.Invoke(this, args);

        internal void OnAppleGrown(CustomNonDerivingEventArgs args) => this.AppleGrown?.Invoke(this, args);

        internal void OnBoolEvent(bool args) => this.BoolEvent?.Invoke(this, args);
    }

    internal class Server2 : IServer2
    {
        public Task<int> MultiplyAsync(int a, int b) => Task.FromResult(a * b);
    }

    internal class ServerOfInternalInterface : IServerInternal
    {
        public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);

        public Task<StreamJsonRpc.Tests.ExternalAssembly.SomeOtherInternalType> SomeMethodAsync()
        {
            return Task.FromResult(new StreamJsonRpc.Tests.ExternalAssembly.SomeOtherInternalType());
        }

        public Task<int> SubtractAsync(int a, int b) => Task.FromResult(a - b);
    }
}
