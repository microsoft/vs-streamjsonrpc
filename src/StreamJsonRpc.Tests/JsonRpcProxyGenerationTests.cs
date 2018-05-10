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
        Task<string> SayHiAsync();

        Task<string> SayHiAsync(string name);

        Task<int> AddAsync(int a, int b);

        Task IncrementAsync();

        Task Dispose();

        Task HeavyWorkAsync(CancellationToken cancellationToken);

        Task<int> HeavyWorkAsync(int param1, CancellationToken cancellationToken);
    }

    public interface IServerWithNonTaskReturnTypes
    {
        int Add(int a, int b);
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
    public void NonTaskReturningMethod()
    {
        var streams = FullDuplexStream.CreateStreams();
        Assert.Throws<ArgumentException>(() => JsonRpc.Attach<IServerWithNonTaskReturnTypes>(streams.Item1));
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
    public void InternalInterface()
    {
        // When implementing internal interfaces work, fill out this test to actually invoke it.
        var streams = FullDuplexStream.CreateStreams();
        Assert.Throws<TypeLoadException>(() => JsonRpc.Attach<IServerInternal>(streams.Item1));
    }

    // TODO:
    // * RPC method names that vary from the CLR method names
    // * events

    internal class Server : IServer
    {
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
    }
}
