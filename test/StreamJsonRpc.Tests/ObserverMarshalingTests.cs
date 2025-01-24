// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

#pragma warning disable CA2214 // Do not call virtual methods in constructors

public abstract partial class ObserverMarshalingTests : TestBase
{
    protected readonly Server server = new Server();
    protected readonly JsonRpc serverRpc;
    protected readonly JsonRpc clientRpc;
    protected readonly IServer client;

    private const string ExceptionMessage = "Some exception";

    protected ObserverMarshalingTests(ITestOutputHelper logger)
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

        Task ReturnThenPushSequence(IObserver<int> observer);

        Task CompleteImmediately(IObserver<int> observer);

        Task ThrowImmediately(IObserver<int> observer);

        Task PushThenThrowImmediately(IObserver<int> observer);

        Task FaultImmediately(IObserver<int> observer);

        Task CompleteTwice(IObserver<int> observer);

        Task PushThenCompleteThenPushAgain(IObserver<int> observer);

        Task PushThenCompleteThenError(IObserver<int> observer);

        Task<IDisposable> Subscribe(IObserver<int> observer);

        Task<IObserver<int>?> GetObserver();
    }

    [Fact]
    public async Task PushCompleteAndReturn()
    {
        var weakRef = await this.PushCompleteAndReturn_Helper();
        await this.AssertWeakReferenceGetsCollectedAsync(weakRef);
    }

    [Fact]
    public async Task ReturnThenPushSequence()
    {
        var observer = new MockObserver<int>();
        await Task.Run(() => this.client.ReturnThenPushSequence(observer)).WithCancellation(this.TimeoutToken);
        var result = await observer.Completion.WithCancellation(this.TimeoutToken);
        await (this.server.FireAndForgetTask ?? Task.CompletedTask).WithCancellation(this.TimeoutToken);
        Assert.Equal(Enumerable.Range(1, 3), result);
    }

    [Fact(Timeout = 2 * 1000)] // TODO: Temporary for development
    public async Task FaultImmediately()
    {
        var observer = new MockObserver<int>();
        await Task.Run(() => this.client.FaultImmediately(observer)).WithCancellation(this.TimeoutToken);
        var ex = await Assert.ThrowsAnyAsync<ApplicationException>(() => observer.Completion);
        Assert.Equal(ExceptionMessage, ex.Message);
        Assert.Empty(observer.ReceivedValues);
    }

    [Fact]
    public async Task CompleteImmediately()
    {
        var observer = new MockObserver<int>();
        await Task.Run(() => this.client.CompleteImmediately(observer)).WithCancellation(this.TimeoutToken);
        await observer.Completion.WithCancellation(this.TimeoutToken);
        Assert.Empty(observer.ReceivedValues);
    }

    [Fact]
    public async Task CompleteTwice()
    {
        var observer = new MockObserver<int>();
        await Task.Run(() => this.client.CompleteTwice(observer)).WithCancellation(this.TimeoutToken);
        await observer.Completion.WithCancellation(this.TimeoutToken);
        Assert.Empty(observer.ReceivedValues);
    }

    [Fact]
    public async Task ServerMethodNotFound_ReleasesClientReference()
    {
        WeakReference weakRef = await this.ServerMethodNotFound_ReleasesClientReference_Helper();
        await this.AssertWeakReferenceGetsCollectedAsync(weakRef);
    }

    [Fact]
    public async Task ServerMethodThrows_ReleasesClientReference()
    {
        WeakReference weakRef = await this.ServerMethodThrows_ReleasesClientReference_Helper();
        await this.AssertWeakReferenceGetsCollectedAsync(weakRef);
    }

    [Fact]
    public async Task PushThenThrowImmediately()
    {
        var observer = new MockObserver<int>();
        await Assert.ThrowsAsync<RemoteInvocationException>(() => Task.Run(() => this.client.PushThenThrowImmediately(observer)).WithCancellation(this.TimeoutToken));
        Assert.False(observer.Completion.IsCompleted);
        await observer.ItemReceived.WaitAsync(this.TimeoutToken);
        Assert.Equal(new[] { 1 }, observer.ReceivedValues);
    }

    [Fact]
    public async Task PushThenCompleteThenPushAgain()
    {
        var observer = new MockObserver<int>();
        await this.client.PushThenCompleteThenPushAgain(observer).WithCancellation(this.TimeoutToken);
        await observer.Completion.WithCancellation(this.TimeoutToken);
        Assert.Equal(new[] { 1 }, observer.ReceivedValues);
    }

    [Fact]
    public async Task PushThenCompleteThenError()
    {
        var observer = new MockObserver<int>();
        await Task.Run(() => this.client.PushThenCompleteThenError(observer)).WithCancellation(this.TimeoutToken);
        await observer.Completion.WithCancellation(this.TimeoutToken);
        Assert.Equal(new[] { 1 }, observer.ReceivedValues);
    }

    [Fact]
    public async Task GetObserver()
    {
        var mockObserver = new MockObserver<int>();
        this.server.Observer = mockObserver;

        IObserver<int>? observer = await Task.Run(() => this.client.GetObserver()).WithCancellation(this.TimeoutToken);
        Assumes.NotNull(observer);
        observer.OnNext(1);
        observer.OnNext(2);
        observer.OnCompleted();

        await mockObserver.Completion;
        Assert.Equal(new[] { 1, 2 }, mockObserver.ReceivedValues);
    }

    [Fact]
    public async Task CancelableSubscription()
    {
        (WeakReference Observer, WeakReference Proxy) weakRefs = await this.CancelableSubscription_Helper().WithCancellation(this.TimeoutToken);
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Observer);
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Proxy);
    }

    protected abstract IJsonRpcMessageFormatter CreateFormatter();

    protected override void Dispose(bool disposing)
    {
        this.serverRpc.Dispose();
        this.client.Dispose();

        if (this.clientRpc.Completion.IsFaulted)
        {
            this.Logger.WriteLine("Client faulted with: " + this.clientRpc.Completion.Exception);
        }

        if (this.serverRpc.Completion.IsFaulted)
        {
            this.Logger.WriteLine("Server faulted with: " + this.serverRpc.Completion.Exception);
        }

        base.Dispose(disposing);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<(WeakReference Observer, WeakReference Proxy)> CancelableSubscription_Helper()
    {
        var mockObserver = new MockObserver<int>();
        var observedValueCount = 0;
        var allValuesObserved = new AsyncManualResetEvent();
        mockObserver.Next += (s, e) =>
        {
            if (++observedValueCount == 2)
            {
                allValuesObserved.Set();
            }
        };

        IDisposable disposable = await this.client.Subscribe(mockObserver).WithCancellation(this.TimeoutToken);
        await allValuesObserved.WaitAsync(this.TimeoutToken);
        await (this.server.FireAndForgetTask ?? Task.CompletedTask).WithCancellation(this.TimeoutToken);
        disposable.Dispose();

        var weakObserver = new WeakReference(mockObserver);
        var weakProxy = new WeakReference(this.server.Observer);
        this.server.Observer = null;
        return (weakObserver, weakProxy);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> ServerMethodNotFound_ReleasesClientReference_Helper()
    {
        var observer = new MockObserver<int>();
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => Task.Run(() => this.clientRpc.InvokeWithCancellationAsync("non-existing", new object?[] { observer }, new Type[] { typeof(IObserver<int>) }, this.TimeoutToken)).WithCancellation(this.TimeoutToken));
        return new WeakReference(observer);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> ServerMethodThrows_ReleasesClientReference_Helper()
    {
        var observer = new MockObserver<int>();
        await Assert.ThrowsAsync<RemoteInvocationException>(() => Task.Run(() => this.client.ThrowImmediately(observer)).WithCancellation(this.TimeoutToken));
        return new WeakReference(observer);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> PushCompleteAndReturn_Helper()
    {
        var observer = new MockObserver<int>();
        await Task.Run(() => this.client.PushCompleteAndReturn(observer)).WithCancellation(this.TimeoutToken);
        await observer.Completion.WithCancellation(this.TimeoutToken);
        ImmutableList<int> result = await observer.Completion;
        Assert.Equal(Enumerable.Range(1, 3), result);
        return new WeakReference(observer);
    }

    protected class Server : IServer
    {
        internal IObserver<int>? Observer { get; set; }

        /// <summary>
        /// Gets an event that is set when the result of <see cref="Subscribe(IObserver{int})"/> has been disposed of.
        /// </summary>
        internal AsyncManualResetEvent SubscriptionIsDisposed { get; } = new AsyncManualResetEvent();

        internal Task? FireAndForgetTask { get; private set; }

        public Task PushCompleteAndReturn(IObserver<int> observer)
        {
            for (int i = 1; i <= 3; i++)
            {
                observer.OnNext(i);
            }

            observer.OnCompleted();
            return Task.CompletedTask;
        }

        public Task ReturnThenPushSequence(IObserver<int> observer)
        {
            this.FireAndForgetTask = Task.Run(async delegate
            {
                for (int i = 1; i <= 3; i++)
                {
                    observer.OnNext(i);
                    await Task.Yield();
                }

                observer.OnCompleted();
            });

            return Task.CompletedTask;
        }

        public Task CompleteImmediately(IObserver<int> observer)
        {
            observer.OnCompleted();
            return Task.CompletedTask;
        }

        public Task ThrowImmediately(IObserver<int> observer) => throw new Exception("By design");

        public Task PushThenThrowImmediately(IObserver<int> observer)
        {
            observer.OnNext(1);
            throw new ApplicationException(ExceptionMessage);
        }

        public Task FaultImmediately(IObserver<int> observer)
        {
            observer.OnError(new ApplicationException(ExceptionMessage));
            return Task.CompletedTask;
        }

        public Task CompleteTwice(IObserver<int> observer)
        {
            observer.OnCompleted();
            Assert.Throws<ObjectDisposedException>(() => observer.OnCompleted());
            return Task.CompletedTask;
        }

        public Task PushThenCompleteThenPushAgain(IObserver<int> observer)
        {
            observer.OnNext(1);
            observer.OnCompleted();
            Assert.Throws<ObjectDisposedException>(() => observer.OnNext(2));
            return Task.CompletedTask;
        }

        public Task PushThenCompleteThenError(IObserver<int> observer)
        {
            observer.OnNext(1);
            observer.OnCompleted();
            Assert.Throws<ObjectDisposedException>(() => observer.OnError(new ApplicationException("Erroring out after completion is illegal.")));
            return Task.CompletedTask;
        }

        public Task<IObserver<int>?> GetObserver() => Task.FromResult(this.Observer);

        public Task<IDisposable> Subscribe(IObserver<int> observer)
        {
            this.Observer = observer;
            this.FireAndForgetTask = Task.Run(delegate
            {
                observer.OnNext(1);
                observer.OnNext(2);
            });
            return Task.FromResult<IDisposable>(new DisposableAction(() =>
            {
                ((IDisposable)observer).Dispose();
                this.SubscriptionIsDisposed.Set();
            }));
        }

        void IDisposable.Dispose()
        {
        }
    }

    protected class MockObserver<T> : IObserver<T>
    {
        private readonly TaskCompletionSource<ImmutableList<T>> completed = new TaskCompletionSource<ImmutableList<T>>();

        internal event EventHandler<T>? Next;

        internal ImmutableList<T> ReceivedValues { get; private set; } = ImmutableList<T>.Empty;

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
