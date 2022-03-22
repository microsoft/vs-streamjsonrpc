// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using Newtonsoft.Json.Serialization;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

public partial class JsonContractResolverTest : TestBase
{
    protected readonly Server server = new Server();
    protected readonly JsonRpc serverRpc;
    protected readonly JsonRpc clientRpc;
    protected readonly IServer client;

    private const string ExceptionMessage = "Some exception";

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

    protected interface IServer : IDisposable
    {
        Task PushCompleteAndReturn(IObserver<int> observer);
    }

    [Fact]
    public async Task PushCompleteAndReturn()
    {
        var observer = new MockObserver<int>();
        await Task.Run(() => this.client.PushCompleteAndReturn(observer)).WithCancellation(this.TimeoutToken);
        await observer.Completion.WithCancellation(this.TimeoutToken);
        ImmutableList<int> result = await observer.Completion;
    }

    protected IJsonRpcMessageFormatter CreateFormatter()
    {
        var formatter = new JsonMessageFormatter();
        formatter.JsonSerializer.ContractResolver = new CamelCasePropertyNamesContractResolver();

        return formatter;
    }

    protected class Server : IServer
    {
        public Task PushCompleteAndReturn(IObserver<int> observer)
        {
            for (int i = 1; i <= 3; i++)
            {
                observer.OnNext(i);
            }

            observer.OnCompleted();
            return Task.CompletedTask;
        }

        void IDisposable.Dispose()
        {
        }
    }

    protected class MockObserver<T> : IObserver<T>
    {
        private readonly TaskCompletionSource<ImmutableList<T>> completed = new TaskCompletionSource<ImmutableList<T>>();

        internal event EventHandler<T>? Next;

        [System.Runtime.Serialization.IgnoreDataMember]
        internal ImmutableList<T> ReceivedValues { get; private set; } = ImmutableList<T>.Empty;

        [System.Runtime.Serialization.IgnoreDataMember]
        internal Task<ImmutableList<T>> Completion => this.completed.Task;

        internal AsyncAutoResetEvent ItemReceived { get; } = new AsyncAutoResetEvent();

        public void OnCompleted() => this.completed.SetResult(this.ReceivedValues);

        public void OnError(Exception error) => this.completed.SetException(error);

        public void OnNext(T value)
        {
            Assert.False(this.completed.Task.IsCompleted);
            this.ReceivedValues = this.ReceivedValues.Add(value);
            this.Next?.Invoke(this, value);
            this.ItemReceived.Set();
        }
    }
}
