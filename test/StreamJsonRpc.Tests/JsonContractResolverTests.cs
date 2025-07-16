// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using Newtonsoft.Json.Serialization;

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

    [JsonRpcContract]
    public partial interface IServer : IDisposable
    {
        Task GiveObserver(IObserver<int> observer);

        Task GiveObserverContainer(Container<IObserver<int>> observerContainer);

        Task<IObserver<int>> GetObserver();

        Task<Container<IObserver<int>>> GetObserverContainer();

        Task GiveMarshalable(IMarshalable marshalable);

        Task GiveMarshalableContainer(Container<IMarshalable> marshalableContainer);

        Task<IMarshalable> GetMarshalable();

        Task<Container<IMarshalable>> GetMarshalableContainer();
    }

    [RpcMarshalable]
    public interface IMarshalable : IDisposable
    {
        void DoSomething();
    }

    [Fact]
    public async Task GiveObserverTest()
    {
        var observer = new MockObserver();
        await Task.Run(() => this.client.GiveObserver(observer), TestContext.Current.CancellationToken).WithCancellation(this.TimeoutToken);
        await observer.Completion.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task GiveObserverContainerTest()
    {
        var observer = new MockObserver();
        await Task.Run(() => this.client.GiveObserverContainer(new Container<IObserver<int>> { Field = observer }), TestContext.Current.CancellationToken).WithCancellation(this.TimeoutToken);
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
        var observer = (await this.client.GetObserverContainer()).Field!;
        observer.OnCompleted();
        await this.server.Observer.Completion.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task GiveMarshalableTest()
    {
        var marshalable = new MockMarshalable();
        await Task.Run(() => this.client.GiveMarshalable(marshalable), TestContext.Current.CancellationToken).WithCancellation(this.TimeoutToken);
        await marshalable.Completion.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task GiveMarshalableContainerTest()
    {
        var marshalable = new MockMarshalable();
        await Task.Run(() => this.client.GiveMarshalableContainer(new Container<IMarshalable> { Field = marshalable }), TestContext.Current.CancellationToken).WithCancellation(this.TimeoutToken);
        await marshalable.Completion.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task GetMarshalableTest()
    {
        var marshalable = await this.client.GetMarshalable();
        marshalable.DoSomething();
        await this.server.Marshalable.Completion.WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task GetMarshalableContainerTest()
    {
        var marshalable = (await this.client.GetMarshalableContainer()).Field!;
        marshalable.DoSomething();
        await this.server.Marshalable.Completion.WithCancellation(this.TimeoutToken);
    }

    private IJsonRpcMessageFormatter CreateFormatter()
    {
        var formatter = new JsonMessageFormatter();
        formatter.JsonSerializer.ContractResolver = new CamelCasePropertyNamesContractResolver();

        return formatter;
    }

    [DataContract]
    public class Container<T>
    where T : class
    {
        [DataMember]
        public T? Field { get; set; }
    }

    private class Server : IServer
    {
        internal MockObserver Observer { get; } = new MockObserver();

        internal MockMarshalable Marshalable { get; } = new MockMarshalable();

        public Task GiveObserver(IObserver<int> observer)
        {
            observer.OnCompleted();
            return Task.CompletedTask;
        }

        public Task GiveObserverContainer(Container<IObserver<int>> observerContainer)
        {
            observerContainer.Field?.OnCompleted();
            return Task.CompletedTask;
        }

        public Task<IObserver<int>> GetObserver() => Task.FromResult<IObserver<int>>(this.Observer);

        public Task<Container<IObserver<int>>> GetObserverContainer() => Task.FromResult(new Container<IObserver<int>>() { Field = this.Observer });

        public Task GiveMarshalable(IMarshalable marshalable)
        {
            marshalable.DoSomething();
            return Task.CompletedTask;
        }

        public Task GiveMarshalableContainer(Container<IMarshalable> marshalableContainer)
        {
            marshalableContainer.Field?.DoSomething();
            return Task.CompletedTask;
        }

        public Task<IMarshalable> GetMarshalable() => Task.FromResult<IMarshalable>(this.Marshalable);

        public Task<Container<IMarshalable>> GetMarshalableContainer() => Task.FromResult(new Container<IMarshalable>() { Field = this.Marshalable });

        void IDisposable.Dispose()
        {
        }
    }

    private class MockObserver : IObserver<int>
    {
        private readonly TaskCompletionSource<bool> completed = new TaskCompletionSource<bool>();

        internal Task Completion => this.completed.Task;

        public void OnCompleted() => this.completed.SetResult(true);

        public void OnError(Exception error) => throw new NotImplementedException();

        public void OnNext(int value) => throw new NotImplementedException();
    }

    private class MockMarshalable : IMarshalable
    {
        private readonly TaskCompletionSource<bool> completed = new TaskCompletionSource<bool>();

        internal Task Completion => this.completed.Task;

        public void DoSomething() => this.completed.SetResult(true);

        public void Dispose()
        {
        }
    }
}
