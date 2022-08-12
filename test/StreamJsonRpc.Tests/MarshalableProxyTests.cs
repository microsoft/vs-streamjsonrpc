// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using MessagePack;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using Newtonsoft.Json;
using StreamJsonRpc;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Tests the proxying of interfaces marked with <see cref="RpcMarshalableAttribute"/>.
/// </summary>
public abstract class MarshalableProxyTests : TestBase
{
    protected readonly Server server = new Server();
    protected readonly JsonRpc serverRpc;
    protected readonly JsonRpc clientRpc;
    protected readonly IServer client;

    protected MarshalableProxyTests(ITestOutputHelper logger)
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

    [RpcMarshalable]
    [JsonConverter(typeof(MarshalableConverter))]
    [MessagePackFormatter(typeof(MarshalableFormatter))]
    public interface IMarshalableAndSerializable : IMarshalable
    {
        private class MarshalableConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return typeof(IMarshalableAndSerializable).IsAssignableFrom(objectType);
            }

            public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
            {
                throw new NotImplementedException();
            }

            public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
            {
                throw new NotImplementedException();
            }
        }

        private class MarshalableFormatter : MessagePack.Formatters.IMessagePackFormatter<IMarshalableAndSerializable>
        {
            public IMarshalableAndSerializable Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                throw new NotImplementedException();
            }

            public void Serialize(ref MessagePackWriter writer, IMarshalableAndSerializable value, MessagePackSerializerOptions options)
            {
                throw new NotImplementedException();
            }
        }
    }

    public interface INonMarshalable : IDisposable
    {
        Task DoSomethingAsync();
    }

    [RpcMarshalable]
    public interface IMarshalable : INonMarshalable
    {
    }

    [RpcMarshalable]
    public interface IGenericMarshalable<T> : IMarshalable
    {
        Task<T> DoSomethingWithParameterAsync(T paremeter);
    }

    public interface INonMarshalableDerivedFromMarshalable : IMarshalable
    {
    }

    [RpcMarshalable]
    public interface INonDisposableMarshalable
    {
    }

    [RpcMarshalable]
    public interface IMarshalableWithProperties : IDisposable
    {
        int Foo { get; }
    }

    [RpcMarshalable]
    public interface IMarshalableWithEvents : IDisposable
    {
        event EventHandler? Foo;
    }

    public interface IServer
    {
        Task<IMarshalable?> GetMarshalableAsync(bool returnNull = false);

        Task<IMarshalable?> GetNonDataContractMarshalableAsync(bool returnNull = false);

        Task<IGenericMarshalable<int>?> GetGenericMarshalableAsync(bool returnNull = false);

        Task<IMarshalableAndSerializable?> GetMarshalableAndSerializableAsync(bool returnNull = false);

        Task AcceptProxyAsync(IMarshalable marshalable, bool dispose = true);

        Task AcceptGenericProxyAsync(IGenericMarshalable<int> marshalable, bool dispose = true);

        Task AcceptMarshalableAndSerializableProxyAsync(IMarshalableAndSerializable marshalable, bool dispose = true);

        Task AcceptProxyContainerAsync(ProxyContainer<IMarshalable> marshalableContainer, bool dispose = true);

        Task AcceptGenericProxyContainerAsync(ProxyContainer<IGenericMarshalable<int>> marshalableContainer, bool dispose = true);

        Task AcceptMarshalableAndSerializableProxyContainerAsync(ProxyContainer<IMarshalableAndSerializable> marshalableContainer, bool dispose = true);

        Task<int> AcceptDataAsync(Data data);

        Task<Data> ReturnDataAsync(int value);

        Task<int> AcceptDataContainerAsync(DataContainer dataContainer);

        Task AcceptNonDisposableMarshalableAsync(INonDisposableMarshalable nonDisposable);

        Task AcceptMarshalableWithPropertiesAsync(IMarshalableWithProperties marshalableWithProperties);

        Task AcceptMarshalableWithEventsAsync(IMarshalableWithEvents marshalableWithEvents);

        Task AcceptNonMarshalableAsync(INonMarshalable nonMarshalable);

        Task AcceptNonMarshalableDerivedFromMarshalablesAsync(INonMarshalableDerivedFromMarshalable nonMarshalable);
    }

    [Fact]
    public async Task NoLeakWhenTransmissionFailsAfterTokenGenerated()
    {
        WeakReference weakRef = await Helper();
        await this.AssertWeakReferenceGetsCollectedAsync(weakRef);

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<WeakReference> Helper()
        {
            var marshalable = new Data();
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.InvokeWithCancellationAsync(
                "someMethod",
                new object?[] { marshalable, new JsonRpcTests.TypeThrowsWhenSerialized() },
                new Type[] { typeof(IMarshalable), typeof(JsonRpcTests.TypeThrowsWhenSerialized) },
                this.TimeoutToken));
            Assert.True(ex is JsonSerializationException || ex is MessagePackSerializationException);
            Assert.True(IsExceptionOrInnerOfType<Exception>(ex, exactTypeMatch: true));

            return new WeakReference(marshalable);
        }
    }

    [Fact]
    public async Task NoLeakWhenServerThrows()
    {
        WeakReference weakRef = await Helper();
        await this.AssertWeakReferenceGetsCollectedAsync(weakRef);

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<WeakReference> Helper()
        {
            var marshalable = new Data();
            await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.clientRpc.InvokeWithCancellationAsync(
                "someMethod",
                new object?[] { marshalable },
                new Type[] { typeof(IMarshalable) },
                this.TimeoutToken));
            return new WeakReference(marshalable);
        }
    }

    [Fact]
    public async Task IMarshalableInNotificationArgumentIsRejected()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => this.clientRpc.NotifyAsync("someMethod", new object?[] { new Data() }, new Type[] { typeof(IMarshalable) }));
        Assert.True(IsExceptionOrInnerOfType<NotSupportedException>(ex));
    }

    [Fact]
    public async Task MarshalableInterfaceMustBeDisposable()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => this.client.AcceptNonDisposableMarshalableAsync(new NonDisposableMarshalable()));
        Assert.True(IsExceptionOrInnerOfType<NotSupportedException>(ex));
    }

    [Fact]
    public async Task MarshalableInterfaceCannotHaveProperties()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => this.client.AcceptMarshalableWithPropertiesAsync(new MarshalableWithProperties()));
        Assert.True(IsExceptionOrInnerOfType<NotSupportedException>(ex));
    }

    [Fact]
    public async Task MarshalableInterfaceCannotHaveEvents()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => this.client.AcceptMarshalableWithEventsAsync(new MarshalableWithEvents()));
        Assert.True(IsExceptionOrInnerOfType<NotSupportedException>(ex));
    }

    [Fact]
    public async Task InterfacesMustBeMarkedAsRpcMarshalable()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => this.client.AcceptNonMarshalableAsync(new NonDataContractMarshalable()));
    }

    [Fact]
    public async Task RpcMarshalableAttributeDoesntAffectDerivedInterfaces()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => this.client.AcceptNonMarshalableDerivedFromMarshalablesAsync(new NonDataContractMarshalable()));
    }

    [Fact]
    public async Task MarshalableReturnValue_DisposeSwallowsSecondCall()
    {
        IMarshalable? proxyMarshalable = await this.client.GetMarshalableAsync();
        Assumes.NotNull(proxyMarshalable);
        proxyMarshalable.Dispose();
        proxyMarshalable.Dispose();
    }

    [Fact]
    public async Task MarshalableReturnValue_IsMarshaledAndLaterCollected()
    {
        var weakRefs = await Helper();
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Proxy);
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Target);

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<(WeakReference Proxy, WeakReference Target)> Helper()
        {
            IDisposable? proxyDisposable = await this.client.GetMarshalableAsync();
            Assert.NotNull(proxyDisposable);
            Data? returnedMarshalable = this.server.ReturnedMarshalable as Data;
            Assumes.NotNull(returnedMarshalable);
            Assert.False(returnedMarshalable.IsDisposed);
            proxyDisposable!.Dispose();
            WeakReference weakProxy = new WeakReference(proxyDisposable);

            await this.server.ReturnedMarshalableDisposed.WaitAsync(this.TimeoutToken);
            WeakReference weakTarget = new WeakReference(this.server.ReturnedMarshalable);
            this.server.ReturnedMarshalable = null;
            return (weakProxy, weakTarget);
        }
    }

    [Fact]
    public async Task MarshalableReturnValue_CanCallMethods()
    {
        IMarshalable? proxy = await this.client.GetMarshalableAsync(returnNull: false);
        Data returnedMarshalable = (Data)this.server.ReturnedMarshalable!;
        Assert.False(returnedMarshalable.DoSomethingCalled);
        await proxy!.DoSomethingAsync();
        Assert.True(returnedMarshalable!.DoSomethingCalled);
    }

    [Fact]
    public async Task NonDataContractMarshalableReturnValue_CanCallMethods()
    {
        IMarshalable? proxy = await this.client.GetNonDataContractMarshalableAsync(returnNull: false);
        NonDataContractMarshalable returnedMarshalable = (NonDataContractMarshalable)this.server.ReturnedMarshalable!;
        Assert.False(returnedMarshalable.DoSomethingCalled);
        await proxy!.DoSomethingAsync();
        Assert.True(returnedMarshalable.DoSomethingCalled);
    }

    [Fact]
    public async Task MarshalableAndSerializableReturnValue_CanCallMethods()
    {
        IMarshalableAndSerializable? proxy = await this.client.GetMarshalableAndSerializableAsync(returnNull: false);
        MarshalableAndSerializable returnedMarshalable = (MarshalableAndSerializable)this.server.ReturnedMarshalable!;
        Assert.False(returnedMarshalable.DoSomethingCalled);
        await proxy!.DoSomethingAsync();
        Assert.True(returnedMarshalable.DoSomethingCalled);
    }

    [Fact]
    public async Task GenericMarshalableReturnValue_CanCallMethods()
    {
        IGenericMarshalable<int>? proxy = await this.client.GetGenericMarshalableAsync(returnNull: false);
        Assert.Equal(99, await proxy!.DoSomethingWithParameterAsync(99));
    }

    [Fact]
    public async Task MarshalableArg_IsMarshaledAndLaterCollected()
    {
        var weakRefs = await Helper();
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Proxy);
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Target);

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<(WeakReference Proxy, WeakReference Target)> Helper()
        {
            var disposed = new AsyncManualResetEvent();
            var strongTarget = new Data(disposed.Set);
            WeakReference weakTarget = new WeakReference(strongTarget);

            await this.client.AcceptProxyAsync(strongTarget);
            await disposed.WaitAsync(this.TimeoutToken);
            Assumes.NotNull(this.server.ReceivedProxy);

            WeakReference weakProxy = new WeakReference(this.server.ReceivedProxy);
            this.server.ReceivedProxy = null;
            return (weakProxy, weakTarget);
        }
    }

    [Fact]
    public async Task MarshalableArg_CanCallMethods()
    {
        var data = new Data();
        await this.client.AcceptProxyAsync(data, false);
        Assert.False(data.DoSomethingCalled);
        await this.server.ReceivedProxy!.DoSomethingAsync();
        Assert.True(data.DoSomethingCalled);
    }

    [Fact]
    public async Task NonDataContractMarshalableArg_CanCallMethods()
    {
        var data = new NonDataContractMarshalable();
        await this.client.AcceptProxyAsync(data, false);
        Assert.False(data.DoSomethingCalled);
        await this.server.ReceivedProxy!.DoSomethingAsync();
        Assert.True(data.DoSomethingCalled);
    }

    [Fact]
    public async Task MarshalableAndSerializableArg_CanCallMethods()
    {
        var data = new MarshalableAndSerializable();
        await this.client.AcceptMarshalableAndSerializableProxyAsync(data, false);
        Assert.False(data.DoSomethingCalled);
        await this.server.ReceivedProxy!.DoSomethingAsync();
        Assert.True(data.DoSomethingCalled);
    }

    [Fact]
    public async Task GenericMarshalableArg_CanCallMethods()
    {
        var data = new Data();
        await this.client.AcceptGenericProxyAsync(data, false);
        Assert.Equal(99,  await ((IGenericMarshalable<int>)this.server.ReceivedProxy!).DoSomethingWithParameterAsync(99));
    }

    [Fact]
    public async Task MarshalableWithinArg_IsMarshaledAndLaterCollected()
    {
        var weakRefs = await Helper();
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Proxy);
        await this.AssertWeakReferenceGetsCollectedAsync(weakRefs.Target);

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<(WeakReference Proxy, WeakReference Target)> Helper()
        {
            var disposed = new AsyncManualResetEvent();
            var strongTarget = new Data(disposed.Set);
            WeakReference weakTarget = new WeakReference(strongTarget);

            await this.client.AcceptProxyContainerAsync(new ProxyContainer<IMarshalable> { Marshalable = strongTarget });
            await disposed.WaitAsync(this.TimeoutToken);
            Assumes.NotNull(this.server.ReceivedProxy);

            WeakReference weakProxy = new WeakReference(this.server.ReceivedProxy);
            this.server.ReceivedProxy = null;
            return (weakProxy, weakTarget);
        }
    }

    [Fact]
    public async Task MarshalableWithinArg_CanCallMethods()
    {
        var data = new Data();
        await this.client.AcceptProxyContainerAsync(new ProxyContainer<IMarshalable>() { Marshalable = data }, false);
        Assert.False(data.DoSomethingCalled);
        await this.server.ReceivedProxy!.DoSomethingAsync();
        Assert.True(data.DoSomethingCalled);
    }

    [Fact]
    public async Task NonDataContractMarshalableWithinArg_CanCallMethods()
    {
        var data = new NonDataContractMarshalable();
        await this.client.AcceptProxyContainerAsync(new ProxyContainer<IMarshalable>() { Marshalable = data }, false);
        Assert.False(data.DoSomethingCalled);
        await this.server.ReceivedProxy!.DoSomethingAsync();
        Assert.True(data.DoSomethingCalled);
    }

    [Fact]
    public async Task MarshalableAndSerializableWithinArg_CanCallMethods()
    {
        var data = new MarshalableAndSerializable();
        await this.client.AcceptMarshalableAndSerializableProxyContainerAsync(new ProxyContainer<IMarshalableAndSerializable>() { Marshalable = data }, false);
        Assert.False(data.DoSomethingCalled);
        await this.server.ReceivedProxy!.DoSomethingAsync();
        Assert.True(data.DoSomethingCalled);
    }

    [Fact]
    public async Task GenericMarshalableWithinArg_CanCallMethods()
    {
        var data = new Data();
        await this.client.AcceptGenericProxyContainerAsync(new ProxyContainer<IGenericMarshalable<int>>() { Marshalable = data }, false);
        Assert.Equal(99, await ((IGenericMarshalable<int>)this.server.ReceivedProxy!).DoSomethingWithParameterAsync(99));
    }

    [Fact]
    public async Task MarshalableReturnValue_Null()
    {
        IMarshalable? proxyMarshalable = await this.client.GetMarshalableAsync(returnNull: true);
        Assert.Null(proxyMarshalable);
    }

    [Fact]
    public async Task IMarshalableDataAsArg_ShouldSerialize()
    {
        Assert.Equal(5, await this.client.AcceptDataAsync(new Data { Value = 5 }));
    }

    [Fact]
    public async Task IMarshalableDataAsObjectWithinArg_ShouldSerialize()
    {
        Assert.Equal(5, await this.client.AcceptDataContainerAsync(new DataContainer { Data = new Data { Value = 5 } }));
    }

    [Fact]
    public async Task IMarshalableDataAsReturnType_ShouldSerialize()
    {
        Data data = await this.client.ReturnDataAsync(5);
        Assert.Equal(5, data.Value);
    }

    [Fact]
    public async Task IMarshalable_MarshaledBackAndForth()
    {
        IMarshalable? proxyMarshalable = await this.client.GetMarshalableAsync().WithCancellation(this.TimeoutToken);
        Assert.NotNull(proxyMarshalable);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => this.client.AcceptProxyAsync(proxyMarshalable!)).WithCancellation(this.TimeoutToken);
        Assert.True(IsExceptionOrInnerOfType<NotSupportedException>(ex));
    }

    [Fact]
    public async Task DisposeOnDisconnect()
    {
        var server = new Server();

        var pipes = FullDuplexStream.CreatePipePair();

        var client = JsonRpc.Attach<IServer>(new LengthHeaderMessageHandler(pipes.Item1, this.CreateFormatter()));
        var clientRpc = ((IJsonRpcClientProxy)client).JsonRpc;

        var serverRpc = new JsonRpc(new LengthHeaderMessageHandler(pipes.Item2, this.CreateFormatter()));
        serverRpc.AddLocalRpcTarget(server);

        serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Verbose);
        clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Verbose);

        serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        serverRpc.StartListening();

        var disposed = new AsyncManualResetEvent();
        Data data = new Data(disposed.Set);
        await client.AcceptProxyAsync(data, dispose: false);

        Assert.False(data.IsDisposed);

        pipes.Item1.AsStream().Dispose();
        await serverRpc.Completion.WithCancellation(this.TimeoutToken);
        await disposed.WaitAsync(this.TimeoutToken);
    }

    protected abstract IJsonRpcMessageFormatter CreateFormatter();

    public class Server : IServer
    {
        internal AsyncManualResetEvent ReturnedMarshalableDisposed { get; } = new AsyncManualResetEvent();

        internal IMarshalable? ReturnedMarshalable { get; set; }

        internal IMarshalable? ReceivedProxy { get; set; }

        public Task<IMarshalable?> GetMarshalableAsync(bool returnNull)
        {
            var marshalable = returnNull ? null : new Data(() => this.ReturnedMarshalableDisposed.Set());
            this.ReturnedMarshalable = marshalable;
            return Task.FromResult<IMarshalable?>(marshalable);
        }

        public Task<IGenericMarshalable<int>?> GetGenericMarshalableAsync(bool returnNull)
        {
            var marshalable = returnNull ? null : new Data(() => this.ReturnedMarshalableDisposed.Set());
            this.ReturnedMarshalable = marshalable;
            return Task.FromResult<IGenericMarshalable<int>?>(marshalable);
        }

        public Task<IMarshalable?> GetNonDataContractMarshalableAsync(bool returnNull = false)
        {
            var marshalable = returnNull ? null : new NonDataContractMarshalable();
            this.ReturnedMarshalable = marshalable;
            return Task.FromResult<IMarshalable?>(marshalable);
        }

        public Task<IMarshalableAndSerializable?> GetMarshalableAndSerializableAsync(bool returnNull = false)
        {
            var marshalable = returnNull ? null : new MarshalableAndSerializable();
            this.ReturnedMarshalable = marshalable;
            return Task.FromResult<IMarshalableAndSerializable?>(marshalable);
        }

        public Task AcceptProxyAsync(IMarshalable marshalable, bool dispose = true)
        {
            this.ReceivedProxy = marshalable;
            if (dispose)
            {
                marshalable.Dispose();
            }

            return Task.CompletedTask;
        }

        public Task AcceptMarshalableAndSerializableProxyAsync(IMarshalableAndSerializable marshalable, bool dispose = true)
        {
            this.ReceivedProxy = marshalable;
            if (dispose)
            {
                marshalable.Dispose();
            }

            return Task.CompletedTask;
        }

        public Task AcceptGenericProxyAsync(IGenericMarshalable<int> marshalable, bool dispose = true)
        {
            this.ReceivedProxy = marshalable;
            if (dispose)
            {
                marshalable.Dispose();
            }

            return Task.CompletedTask;
        }

        public Task AcceptProxyContainerAsync(ProxyContainer<IMarshalable> marshalableContainer, bool dispose = true)
        {
            this.ReceivedProxy = marshalableContainer.Marshalable;
            if (dispose)
            {
                marshalableContainer.Marshalable?.Dispose();
            }

            return Task.CompletedTask;
        }

        public Task AcceptMarshalableAndSerializableProxyContainerAsync(ProxyContainer<IMarshalableAndSerializable> marshalableContainer, bool dispose = true)
        {
            this.ReceivedProxy = marshalableContainer.Marshalable;
            if (dispose)
            {
                marshalableContainer.Marshalable?.Dispose();
            }

            return Task.CompletedTask;
        }

        public Task AcceptGenericProxyContainerAsync(ProxyContainer<IGenericMarshalable<int>> marshalableContainer, bool dispose = true)
        {
            this.ReceivedProxy = marshalableContainer.Marshalable;
            if (dispose)
            {
                marshalableContainer.Marshalable?.Dispose();
            }

            return Task.CompletedTask;
        }

        public Task<int> AcceptDataAsync(Data data) => Task.FromResult(data.Value);

        public Task<Data> ReturnDataAsync(int value) => Task.FromResult(new Data { Value = value });

        public Task<int> AcceptDataContainerAsync(DataContainer dataContainer) => Task.FromResult(dataContainer.Data?.Value ?? 0);

        public Task AcceptNonDisposableMarshalableAsync(INonDisposableMarshalable nonDisposable) => Task.CompletedTask;

        public Task AcceptMarshalableWithPropertiesAsync(IMarshalableWithProperties marshalableWithProperties) => Task.CompletedTask;

        public Task AcceptMarshalableWithEventsAsync(IMarshalableWithEvents marshalableWithEvents) => Task.CompletedTask;

        public Task AcceptNonMarshalableAsync(INonMarshalable nonMarshalable) => Task.CompletedTask;

        public Task AcceptNonMarshalableDerivedFromMarshalablesAsync(INonMarshalableDerivedFromMarshalable nonMarshalable) => Task.CompletedTask;
    }

    [DataContract]
    public class ProxyContainer<T>
    {
        [DataMember]
        public T? Marshalable { get; set; }
    }

    [DataContract]
    public class DataContainer
    {
        [DataMember]
        public Data? Data { get; set; }
    }

    [DataContract]
    public class Data : IGenericMarshalable<int>
    {
        private readonly Action? disposeAction;

        public Data()
            : this(null)
        {
        }

        public Data(Action? disposeAction)
        {
            this.disposeAction = disposeAction;
        }

        [DataMember]
        public int Value { get; set; }

        public bool IsDisposed { get; private set; }

        public bool DoSomethingCalled { get; private set; }

        public void Dispose()
        {
            if (this.IsDisposed is false)
            {
                this.IsDisposed = true;
                this.disposeAction?.Invoke();
            }
        }

        public Task<int> DoSomethingWithParameterAsync(int paremeter)
            => Task.FromResult(paremeter);

        public Task DoSomethingAsync()
        {
            this.DoSomethingCalled = true;
            return Task.CompletedTask;
        }
    }

    public class NonDataContractMarshalable : INonMarshalableDerivedFromMarshalable
    {
        public bool DoSomethingCalled { get; private set; }

        public void Dispose()
        {
        }

        public Task DoSomethingAsync()
        {
            this.DoSomethingCalled = true;
            return Task.CompletedTask;
        }
    }

    public class MarshalableAndSerializable : IMarshalableAndSerializable
    {
        public bool DoSomethingCalled { get; private set; }

        public void Dispose()
        {
        }

        public Task DoSomethingAsync()
        {
            this.DoSomethingCalled = true;
            return Task.CompletedTask;
        }
    }

    public class NonDisposableMarshalable : INonDisposableMarshalable
    {
    }

    public class MarshalableWithProperties : IMarshalableWithProperties
    {
        public int Foo { get; }

        public void Dispose()
        {
        }
    }

    public class MarshalableWithEvents : IMarshalableWithEvents
    {
#pragma warning disable CS0067 // The event is never used
        public event EventHandler? Foo;
#pragma warning restore CS0067 // The event is never used

        public void Dispose()
        {
        }
    }
}
