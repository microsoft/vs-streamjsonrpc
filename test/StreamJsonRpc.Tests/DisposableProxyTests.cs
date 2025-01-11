// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

#pragma warning disable CA2214 // Do not call virtual methods in constructors

/// <summary>
/// Tests the proxying of <see cref="IDisposable"/> values.
/// </summary>
public abstract class DisposableProxyTests : TestBase
{
    protected readonly Server server = new Server();
    protected readonly JsonRpc serverRpc;
    protected readonly JsonRpc clientRpc;
    protected readonly IServer client;

    protected DisposableProxyTests(ITestOutputHelper logger)
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

    public interface IServer
    {
        Task<IDisposable?> GetDisposableAsync(bool returnNull = false);

        Task AcceptProxyAsync(IDisposable disposable);

        Task AcceptProxyContainerAsync(ProxyContainer disposableContainer);

        Task<int> AcceptDataAsync(Data data);

        Task<Data> ReturnDataAsync(int value);

        Task<int> AcceptDataContainerAsync(DataContainer dataContainer);
    }

    protected abstract Type FormatterExceptionType { get; }

    [Fact]
    public async Task NoLeakWhenTransmissionFailsAfterTokenGenerated()
    {
        WeakReference weakRef = await this.NoLeakWhenTransmissionFailsAfterTokenGenerated_Helper();
        await this.AssertWeakReferenceGetsCollectedAsync(weakRef);
    }

    [Fact]
    public async Task NoLeakWhenServerThrows()
    {
        WeakReference weakRef = await this.NoLeakWhenServerThrows_Helper();
        await this.AssertWeakReferenceGetsCollectedAsync(weakRef);
    }

    [Fact]
    public async Task IDisposableInNotificationArgumentIsRejected()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.NotifyAsync("someMethod", new object?[] { new DisposableAction(() => { }) }, new Type[] { typeof(IDisposable) }));
        Assert.True(IsExceptionOrInnerOfType<NotSupportedException>(ex));
    }

    [Fact(Timeout = 2 * 1000)] // TODO: Temporary for development
    public async Task DisposableReturnValue_DisposeSwallowsSecondCall()
    {
        IDisposable? proxyDisposable = await this.client.GetDisposableAsync();
        Assumes.NotNull(proxyDisposable);
        proxyDisposable.Dispose();
        proxyDisposable.Dispose();
    }

    [Fact(Timeout = 2 * 1000)] // TODO: Temporary for development
    public async Task DisposableReturnValue_IsMarshaledAndLaterCollected()
    {
        var weakRefs = await this.DisposableReturnValue_Helper();
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Proxy);
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Target);
    }

    [Fact]
    public async Task DisposableArg_IsMarshaledAndLaterCollected()
    {
        var weakRefs = await this.DisposableArg_Helper();
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Proxy);
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Target);
    }

    [Fact]
    public async Task DisposableWithinArg_IsMarshaledAndLaterCollected()
    {
        var weakRefs = await this.DisposableWithinArg_Helper();
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Proxy);
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Target);
    }

    [Fact(Timeout = 2 * 1000)] // TODO: Temporary for development
    public async Task DisposableReturnValue_Null()
    {
        IDisposable? proxyDisposable = await this.client.GetDisposableAsync(returnNull: true);
        Assert.Null(proxyDisposable);
    }

    [Fact]
    public async Task IDisposableDataAsArg_ShouldSerialize()
    {
        Assert.Equal(5, await this.client.AcceptDataAsync(new Data { Value = 5 }));
    }

    [Fact]
    public async Task IDisposableDataAsObjectWithinArg_ShouldSerialize()
    {
        Assert.Equal(5, await this.client.AcceptDataContainerAsync(new DataContainer { Data = new Data { Value = 5 } }));
    }

    [Fact]
    public async Task IDisposableDataAsReturnType_ShouldSerialize()
    {
        Data data = await this.client.ReturnDataAsync(5);
        Assert.Equal(5, data.Value);
    }

    [Fact]
    public async Task IDisposable_MarshaledBackAndForth()
    {
        IDisposable? disposable = await this.client.GetDisposableAsync().WithCancellation(this.TimeoutToken);
        Assert.NotNull(disposable);
        await this.client.AcceptProxyAsync(disposable).WithCancellation(this.TimeoutToken);
        Assert.Same(this.server.ReturnedDisposable, this.server.ReceivedProxy);
    }

    protected abstract IJsonRpcMessageFormatter CreateFormatter();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<(WeakReference Proxy, WeakReference Target)> DisposableReturnValue_Helper()
    {
        IDisposable? proxyDisposable = await this.client.GetDisposableAsync();
        Assert.NotNull(proxyDisposable);
        Assumes.NotNull(this.server.ReturnedDisposable);
        Assert.False(this.server.ReturnedDisposable.IsDisposed);
        proxyDisposable!.Dispose();
        WeakReference weakProxy = new WeakReference(proxyDisposable);

        await this.server.ReturnedDisposableDisposed.WaitAsync(this.TimeoutToken);
        WeakReference weakTarget = new WeakReference(this.server.ReturnedDisposable);
        this.server.ReturnedDisposable = null;
        return (weakProxy, weakTarget);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<(WeakReference Proxy, WeakReference Target)> DisposableArg_Helper()
    {
        var disposed = new AsyncManualResetEvent();
        var strongTarget = new DisposableAction(disposed.Set);
        WeakReference weakTarget = new WeakReference(strongTarget);

        await this.client.AcceptProxyAsync(strongTarget);
        await disposed.WaitAsync(this.TimeoutToken);
        Assumes.NotNull(this.server.ReceivedProxy);

        WeakReference weakProxy = new WeakReference(this.server.ReceivedProxy);
        this.server.ReceivedProxy = null;
        return (weakProxy, weakTarget);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<(WeakReference Proxy, WeakReference Target)> DisposableWithinArg_Helper()
    {
        var disposed = new AsyncManualResetEvent();
        var strongTarget = new DisposableAction(disposed.Set);
        WeakReference weakTarget = new WeakReference(strongTarget);

        await this.client.AcceptProxyContainerAsync(new ProxyContainer { Disposable = strongTarget });
        await disposed.WaitAsync(this.TimeoutToken);
        Assumes.NotNull(this.server.ReceivedProxy);

        WeakReference weakProxy = new WeakReference(this.server.ReceivedProxy);
        this.server.ReceivedProxy = null;
        return (weakProxy, weakTarget);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> NoLeakWhenTransmissionFailsAfterTokenGenerated_Helper()
    {
        var disposable = new DisposableAction(null);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.InvokeWithCancellationAsync(
            "someMethod",
            new object?[] { disposable, new JsonRpcTests.TypeThrowsWhenSerialized() },
            new Type[] { typeof(IDisposable), typeof(JsonRpcTests.TypeThrowsWhenSerialized) },
            this.TimeoutToken));
        Assert.IsAssignableFrom(this.FormatterExceptionType, ex);
        Assert.True(IsExceptionOrInnerOfType<Exception>(ex, exactTypeMatch: true));

        return new WeakReference(disposable);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<WeakReference> NoLeakWhenServerThrows_Helper()
    {
        var disposable = new DisposableAction(null);
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeWithCancellationAsync(
            "someMethod",
            new object?[] { disposable },
            new Type[] { typeof(IDisposable) },
            this.TimeoutToken));
        return new WeakReference(disposable);
    }

    public class Server : IServer
    {
        internal AsyncManualResetEvent ReturnedDisposableDisposed { get; } = new AsyncManualResetEvent();

        internal IDisposableObservable? ReturnedDisposable { get; set; }

        internal IDisposable? ReceivedProxy { get; set; }

        public Task<IDisposable?> GetDisposableAsync(bool returnNull) => Task.FromResult<IDisposable?>(returnNull ? null : this.ReturnedDisposable = new DisposableAction(() => this.ReturnedDisposableDisposed.Set()));

        public Task AcceptProxyAsync(IDisposable disposable)
        {
            this.ReceivedProxy = disposable;
            disposable.Dispose();
            return Task.CompletedTask;
        }

        public Task AcceptProxyContainerAsync(ProxyContainer disposableContainer)
        {
            this.ReceivedProxy = disposableContainer.Disposable;
            disposableContainer.Disposable?.Dispose();
            return Task.CompletedTask;
        }

        public Task<int> AcceptDataAsync(Data data) => Task.FromResult(data.Value);

        public Task<Data> ReturnDataAsync(int value) => Task.FromResult(new Data { Value = value });

        public Task<int> AcceptDataContainerAsync(DataContainer dataContainer) => Task.FromResult(dataContainer.Data?.Value ?? 0);
    }

    [DataContract]
    public class ProxyContainer
    {
        [DataMember]
        public IDisposable? Disposable { get; set; }
    }

    [DataContract]
    public class DataContainer
    {
        [DataMember]
        public Data? Data { get; set; }
    }

    [DataContract]
    public class Data : IDisposable
    {
        [DataMember]
        public int Value { get; set; }

        public void Dispose()
        {
        }
    }
}
