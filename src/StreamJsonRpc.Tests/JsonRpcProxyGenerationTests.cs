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
    private IServer clientRpc;

    public JsonRpcProxyGenerationTests(ITestOutputHelper logger)
        : base(logger)
    {
        var streams = FullDuplexStream.CreateStreams();
        this.serverStream = streams.Item1;
        this.clientStream = streams.Item2;

        this.clientRpc = JsonRpc.Attach<IServer>(this.clientStream);

        this.server = new Server();
        this.serverRpc = JsonRpc.Attach(this.serverStream, this.server);
    }

    public interface IServer
    {
        event EventHandler ItHappened;

        event EventHandler<CustomEventArgs> TreeGrown;

        Task<string> SayHiAsync();

        Task<string> SayHiAsync(string name);

        Task<int> AddAsync(int a, int b);

        Task IncrementAsync();

        Task Dispose();

        Task HeavyWorkAsync(CancellationToken cancellationToken);

        Task<int> HeavyWorkAsync(int param1, CancellationToken cancellationToken);

        [JsonRpcMethod("AnotherName")]
        Task<string> ARoseByAsync(string name);
    }

    public interface IServer2
    {
        Task<int> MultiplyAsync(int a, int b);
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

    internal interface IServerInternal
    {
        Task AddAsync(int a, int b);
    }

    [Fact]
    public void ProxyTypeIsReused()
    {
        var streams = FullDuplexStream.CreateStreams();
        var clientRpc = JsonRpc.Attach<IServer>(streams.Item1);
        Assert.IsType(this.clientRpc.GetType(), clientRpc);
    }

    [Fact]
    public async Task CallMethod_String_String()
    {
        Assert.Equal("Hi, Andrew!", await this.clientRpc.SayHiAsync("Andrew"));
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
    public void ImplementsIDisposable()
    {
        var disposableClient = (IDisposable)this.clientRpc;
        disposableClient.Dispose();
        Assert.True(this.clientStream.IsDisposed);
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
    public async Task CallMethod_CancellationToken_int()
    {
        await this.clientRpc.HeavyWorkAsync(CancellationToken.None);
        Assert.Equal(1, this.server.Counter);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.clientRpc.HeavyWorkAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task CallMethod_intCancellationToken_int()
    {
        Assert.Equal(123, await this.clientRpc.HeavyWorkAsync(123, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.clientRpc.HeavyWorkAsync(456, new CancellationToken(canceled: true)));
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
    public void InstanceProxiesDoNotImplementIDisposable()
    {
        var streams = FullDuplexStream.CreateStreams();
        var server = new Server();
        var serverRpc = JsonRpc.Attach(streams.Item2, server);

        var clientRpc = new JsonRpc(streams.Item1, streams.Item1);
        var client1 = clientRpc.Attach<IServer>();
        Assert.IsNotType(typeof(IDisposable), client1);
    }

    [Fact]
    public void InternalInterface()
    {
        // When implementing internal interfaces work, fill out this test to actually invoke it.
        var streams = FullDuplexStream.CreateStreams();
        Assert.Throws<TypeLoadException>(() => JsonRpc.Attach<IServerInternal>(streams.Item1));
    }

#if NET452 || NET461 || NETCOREAPP2_0
    [Fact]
    public async Task RPCMethodNameSubstitution()
    {
        Assert.Equal("ANDREW", await this.clientRpc.ARoseByAsync("andrew"));
    }
#endif

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

    public class EmptyClass
    {
    }

    public class CustomEventArgs : EventArgs
    {
        public int Seeds { get; set; }
    }

    internal class Server : IServer, IServer2
    {
        public event EventHandler ItHappened;

        public event EventHandler<CustomEventArgs> TreeGrown;

        public int Counter { get; set; }

        public Task<string> SayHiAsync() => Task.FromResult("Hi!");

        public Task<string> SayHiAsync(string name) => Task.FromResult($"Hi, {name}!");

        public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);

        public Task IncrementAsync()
        {
            this.Counter++;
            return TplExtensions.CompletedTask;
        }

        public Task Dispose()
        {
            this.Counter--;
            return TplExtensions.CompletedTask;
        }

        public Task HeavyWorkAsync(CancellationToken cancellationToken)
        {
            this.Counter++;
            cancellationToken.ThrowIfCancellationRequested();
            return TplExtensions.CompletedTask;
        }

        public Task<int> HeavyWorkAsync(int param1, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(param1);
        }

        public Task<int> MultiplyAsync(int a, int b) => Task.FromResult(a * b);

        public Task<string> ARoseByAsync(string name) => Task.FromResult(name.ToUpperInvariant());

        internal void OnItHappened(EventArgs args) => this.ItHappened?.Invoke(this, args);

        internal void OnTreeGrown(CustomEventArgs args) => this.TreeGrown?.Invoke(this, args);
    }
}
