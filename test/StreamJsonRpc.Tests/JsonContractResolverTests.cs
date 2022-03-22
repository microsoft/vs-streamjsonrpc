// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using Newtonsoft.Json.Serialization;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public partial class JsonContractResolverTest : TestBase
{
    private readonly Server server = new Server();
    private readonly JsonRpc serverRpc;
    private readonly JsonRpc clientRpc;
    private readonly IServer client;

    public JsonContractResolverTest(ITestOutputHelper logger)
        : base(logger)
    {
        var pipes = FullDuplexStream.CreatePipePair();

        this.client = JsonRpc.Attach<IServer>(new LengthHeaderMessageHandler(pipes.Item1, this.CreateFormatter()));
        this.clientRpc = ((IJsonRpcClientProxy)this.client).JsonRpc;

        this.serverRpc = new JsonRpc(new LengthHeaderMessageHandler(pipes.Item2, this.CreateFormatter()));
        this.serverRpc.AddLocalRpcTarget(this.server);

        this.serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Verbose);
        this.clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Verbose);

        this.serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        this.clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        this.serverRpc.StartListening();
    }

    private interface IServer : IDisposable
    {
        Task GiveObserver(IObserver<int> observer);

        Task GiveObserverContainer(ObserverContainer observerContainer);

        Task<IObserver<int>> GetObserver();

        Task<ObserverContainer> GetObserverContainer();
    }

    [Fact]
    public async Task GiveObserverTest()
    {
        var observer = new MockObserver<int>();
        await Task.Run(() => this.client.GiveObserver(observer)).WithCancellation(this.TimeoutToken);
        await observer.Completion.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task GiveObserverContainerTest()
    {
        var observer = new MockObserver<int>();
        await Task.Run(() => this.client.GiveObserverContainer(new ObserverContainer { Observer = observer })).WithCancellation(this.TimeoutToken);
        await observer.Completion.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task GetObserverTest()
    {
        var observer = await this.client.GetObserver();
        observer.OnCompleted();
        await this.server.Observer.Completion.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task GetObserverContainerTest()
    {
        var observer = (await this.client.GetObserverContainer()).Observer!;
        observer.OnCompleted();
        await this.server.Observer.Completion.WithCancellation(this.TimeoutToken);
    }

    private IJsonRpcMessageFormatter CreateFormatter()
    {
        var formatter = new JsonMessageFormatter();
        formatter.JsonSerializer.ContractResolver = new CamelCasePropertyNamesContractResolver();

        return formatter;
    }

    private class Server : IServer
    {
        internal MockObserver<int> Observer { get; } = new MockObserver<int>();

        public Task GiveObserver(IObserver<int> observer)
        {
            observer.OnCompleted();
            return Task.CompletedTask;
        }

        public Task GiveObserverContainer(ObserverContainer observerContainer)
        {
            observerContainer.Observer?.OnCompleted();
            return Task.CompletedTask;
        }

        public Task<IObserver<int>> GetObserver() => Task.FromResult<IObserver<int>>(this.Observer);

        public Task<ObserverContainer> GetObserverContainer() => Task.FromResult(new ObserverContainer() { Observer = this.Observer });

        void IDisposable.Dispose()
        {
        }
    }

    [DataContract]
    private class ObserverContainer
    {
        [DataMember]
        public IObserver<int>? Observer { get; set; }
    }

    private class MockObserver<T> : IObserver<T>
    {
        private readonly TaskCompletionSource<bool> completed = new TaskCompletionSource<bool>();

        internal event EventHandler<T>? Next;

        [System.Runtime.Serialization.IgnoreDataMember]
        internal Task Completion => this.completed.Task;

        public void OnCompleted() => this.completed.SetResult(true);

        public void OnError(Exception error) => throw new NotImplementedException();

        public void OnNext(T value) => throw new NotImplementedException();
    }
}
