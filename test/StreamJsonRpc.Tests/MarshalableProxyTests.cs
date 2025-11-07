// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using MessagePack;
using Microsoft.VisualStudio.Threading;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using Newtonsoft.Json;

/// <summary>
/// Tests the proxying of interfaces marked with <see cref="RpcMarshalableAttribute"/>.
/// </summary>
public abstract partial class MarshalableProxyTests : TestBase
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

    [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [JsonConverter(typeof(MarshalableConverter))]
    [MessagePackFormatter(typeof(MarshalableFormatter))]
    [MessagePackConverter(typeof(MarshalableNerdbankConverter))]
    public partial interface IMarshalableAndSerializable : IMarshalable
    {
        internal class MarshalableNerdbankConverter : Nerdbank.MessagePack.MessagePackConverter<IMarshalableAndSerializable>
        {
            public override IMarshalableAndSerializable? Read(ref Nerdbank.MessagePack.MessagePackReader reader, Nerdbank.MessagePack.SerializationContext context)
            {
                throw new NotImplementedException();
            }

            public override void Write(ref Nerdbank.MessagePack.MessagePackWriter writer, in IMarshalableAndSerializable? value, Nerdbank.MessagePack.SerializationContext context)
            {
                throw new NotImplementedException();
            }
        }

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
            public IMarshalableAndSerializable Deserialize(ref MessagePack.MessagePackReader reader, MessagePackSerializerOptions options)
            {
                throw new NotImplementedException();
            }

            public void Serialize(ref MessagePack.MessagePackWriter writer, IMarshalableAndSerializable value, MessagePackSerializerOptions options)
            {
                throw new NotImplementedException();
            }
        }
    }

    public interface INonMarshalable : IDisposable
    {
        Task DoSomethingAsync();
    }

    [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IMarshalable : INonMarshalable
    {
    }

    [RpcMarshalable(CallScopedLifetime = true), GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IMarshalableWithCallScopedLifetime : IMarshalable
    {
    }

    [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [JsonRpcProxy<IGenericMarshalable<int>>]
    public partial interface IGenericMarshalable<T> : IMarshalable
    {
        Task<T> DoSomethingWithParameterAsync(T parameter);
    }

    public interface INonMarshalableDerivedFromMarshalable : IMarshalable
    {
    }

    [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
#pragma warning disable StreamJsonRpc0005 // RpcMarshalable are IDisposable -- runtime fail mode test
    public partial interface INonDisposableMarshalable
#pragma warning restore StreamJsonRpc0005 // RpcMarshalable are IDisposable -- runtime fail mode test
    {
    }

    [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IMarshalableWithProperties : IDisposable
    {
#pragma warning disable StreamJsonRpc0012 // Unsupported member -- runtime fail mode test
        int Foo { get; }
#pragma warning restore StreamJsonRpc0012 // Unsupported member
    }

    [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IMarshalableWithEvents : IDisposable
    {
#pragma warning disable StreamJsonRpc0012 // Unsupported member -- runtime fail mode test
        event EventHandler? Foo;
#pragma warning restore StreamJsonRpc0012 // Unsupported member -- runtime fail mode test
    }

    [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [RpcMarshalableOptionalInterface(1, typeof(IMarshalableSubType1))]
    [RpcMarshalableOptionalInterface(2, typeof(IMarshalableSubType2))]
    [RpcMarshalableOptionalInterface(3, typeof(IMarshalableSubType1Extended))]
    [RpcMarshalableOptionalInterface(4, typeof(IMarshalableNonExtendingBase))]
    [RpcMarshalableOptionalInterface(5, typeof(IMarshalableSubTypesCombined))]
    [RpcMarshalableOptionalInterface(6, typeof(IMarshalableSubTypeWithIntermediateInterface))]
    [RpcMarshalableOptionalInterface(7, typeof(IMarshalableSubTypeWithIntermediateInterface2))]
    public partial interface IMarshalableWithOptionalInterfaces : IDisposable
    {
        Task<int> GetAsync(int value);

        [JsonRpcMethod("RenamedAsync")]
        Task<string> ToBeRenamedAsync(string s);
    }

    [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [RpcMarshalableOptionalInterface(1, typeof(IMarshalableSubTypeWithIntermediateInterface2))]
    [RpcMarshalableOptionalInterface(2, typeof(IMarshalableSubTypeWithIntermediateInterface))]
    public partial interface IMarshalableWithOptionalInterfaces2 : IMarshalableWithOptionalInterfaces
    {
    }

    [RpcMarshalable(IsOptional = true), GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IMarshalableNonExtendingBase : IDisposable
    {
        Task<int> GetPlusFourAsync(int value);
    }

    // This is non-RpcMarshalable
    public interface IMarshalableSubTypeIntermediateInterface : IMarshalableWithOptionalInterfaces2
    {
        Task<int> GetPlusOneAsync(int value);

        Task<int> GetPlusTwoAsync(int value);
    }

    [RpcMarshalable(IsOptional = true), GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IMarshalableSubTypeWithIntermediateInterface : IMarshalableSubTypeIntermediateInterface
    {
        new Task<int> GetPlusTwoAsync(int value);

        Task<int> GetPlusThreeAsync(int value);
    }

    [RpcMarshalable(IsOptional = true), GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IMarshalableSubTypeWithIntermediateInterface2 : IMarshalableSubTypeIntermediateInterface
    {
        new Task<int> GetPlusTwoAsync(int value);
    }

    [RpcMarshalable(IsOptional = true), GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IMarshalableSubType1 : IMarshalableWithOptionalInterfaces2
    {
        Task<int> GetPlusOneAsync(int value);

        Task<int> GetMinusOneAsync(int value);
    }

    [RpcMarshalable(IsOptional = true), GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IMarshalableSubType1Extended : IMarshalableSubType1
    {
        new Task<int> GetAsync(int value);

        new Task<int> GetPlusOneAsync(int value);

        Task<int> GetPlusTwoAsync(int value);

        Task<int> GetPlusThreeAsync(int value);

        new Task<int> GetMinusOneAsync(int value);

        Task<int> GetMinusTwoAsync(int value);
    }

    [RpcMarshalable(IsOptional = true), GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IMarshalableSubTypesCombined : IMarshalableSubType1Extended, IMarshalableSubType2, IMarshalableNonExtendingBase
    {
        Task<int> GetPlusFiveAsync(int value);
    }

    [RpcMarshalable(IsOptional = true), GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    [RpcMarshalableOptionalInterface(1, typeof(IMarshalableSubType2Extended))]
    public partial interface IMarshalableSubType2 : IMarshalableWithOptionalInterfaces2
    {
        Task<int> GetPlusTwoAsync(int value);

        Task<int> GetMinusTwoAsync(int value);
    }

    [RpcMarshalable(IsOptional = true), GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IMarshalableSubType2Extended : IMarshalableSubType2
    {
        Task<int> GetPlusThreeAsync(int value);
    }

    [RpcMarshalable, TypeShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IMarshalableUnknownSubType : IMarshalableWithOptionalInterfaces2
    {
    }

    [JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
    public partial interface IServer
    {
        Task<IMarshalable?> GetMarshalableAsync(bool returnNull = false);

        Task<IMarshalableWithOptionalInterfaces?> GetMarshalableWithOptionalInterfacesAsync();

        Task<IMarshalableWithOptionalInterfaces2?> GetMarshalableWithOptionalInterfaces2Async();

        Task<IMarshalableSubType2?> GetMarshalableSubType2Async();

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

        Task CallScopedMarshalableAsync(IMarshalableWithCallScopedLifetime marshalable);

        Task<IMarshalableWithCallScopedLifetime> ReturnCallScopedObjectAsync();

        IAsyncEnumerable<int> CallScopedMarshalableReturnsAsyncEnumerable(IMarshalableWithCallScopedLifetime marshalable);

        Task CallScopedMarshalableThrowsWithAsyncEnumerable(IMarshalableWithCallScopedLifetime marshalable);
    }

    protected abstract Type FormatterExceptionType { get; }

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
            Assert.IsAssignableFrom(this.FormatterExceptionType, ex);
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
        Assert.SkipWhen(this is MarshalableProxyNerdbankMessagePackTests, "Events are not yet detectable by PolyType."); // remove when https://github.com/eiriktsarpalis/PolyType/issues/226 is fixed.
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
        Assert.Equal(99, await ((IGenericMarshalable<int>)this.server.ReceivedProxy!).DoSomethingWithParameterAsync(99));
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
        await this.client.AcceptProxyAsync(proxyMarshalable).WithCancellation(this.TimeoutToken);
        Assert.Same(this.server.ReturnedMarshalable, this.server.ReceivedProxy);
    }

    [Fact]
    public async Task MarshableDisposedAfterConnection()
    {
        IMarshalable? proxyMarshalable = await this.client.GetMarshalableAsync().WithCancellation(this.TimeoutToken);
        Assert.NotNull(proxyMarshalable);
        this.clientRpc.Dispose();
        proxyMarshalable.Dispose();
    }

    [Fact]
    public async Task IMarshalable_MarshaledAndForwarded()
    {
        IMarshalable? proxyMarshalable = await this.client.GetMarshalableAsync().WithCancellation(this.TimeoutToken);
        Assert.NotNull(proxyMarshalable);

        // Try to send the proxy to a *different* server. This should fail.
        var pipes = FullDuplexStream.CreatePipePair();
        IServer client2 = JsonRpc.Attach<IServer>(new LengthHeaderMessageHandler(pipes.Item1, this.CreateFormatter()));
        JsonRpc clientRpc2 = ((IJsonRpcClientProxy)this.client).JsonRpc;
        Server server2 = new();
        JsonRpc serverRpc2 = new JsonRpc(new LengthHeaderMessageHandler(pipes.Item2, this.CreateFormatter()));
        serverRpc2.AddLocalRpcTarget(server2);
        serverRpc2.TraceSource = new TraceSource("Server2", SourceLevels.Verbose);
        clientRpc2.TraceSource = new TraceSource("Client2", SourceLevels.Verbose);
        serverRpc2.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        clientRpc2.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        serverRpc2.StartListening();

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => client2.AcceptProxyAsync(proxyMarshalable)).WithCancellation(this.TimeoutToken);
        this.Logger.WriteLine("Received exception: {0}", ex);
        Assert.True(IsExceptionOrInnerOfType<NotSupportedException>(ex));
    }

    [Fact]
    public async Task OneObjectMarshalledTwiceHasIndependentLifetimes()
    {
        // The method we call twice returns the same object each time,
        // but JsonRpc doesn't recognize this and assigns a unique token and proxy each time.
        IMarshalable proxy1 = (await this.client.GetMarshalableAsync().WithCancellation(this.TimeoutToken))!;
        IMarshalable proxy2 = (await this.client.GetMarshalableAsync().WithCancellation(this.TimeoutToken))!;

        // Verify that although the original object is the same, the proxies are different.
        Assert.NotSame(proxy1, proxy2);

        // Verify that the proxies both work.
        await proxy1.DoSomethingAsync();
        await proxy2.DoSomethingAsync();

        // Verify that disposing one proxy doesn't break the connection the other proxy has with the original object.
        // For our purposes, the original object's Dispose isn't so self-destructive that it would break proxy2's
        // ability to call methods on it.
        // In doing so, wait for the dispose to propagate to the remote party.
        proxy1.Dispose();
        await this.server.ReturnedMarshalableDisposed.WaitAsync(this.TimeoutToken);
        await proxy2.DoSomethingAsync();
        proxy2.Dispose();
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

    [Fact]
    public async Task RpcMarshalableOptionalInterface()
    {
        this.server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableWithOptionalInterfaces();
        IMarshalableWithOptionalInterfaces? proxy = await this.client.GetMarshalableWithOptionalInterfacesAsync();
        Assert.Equal(1, await proxy!.GetAsync(1));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType1));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType2));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType2Extended));

        this.server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableSubType1();
        IMarshalableSubType1? proxy1 = (await this.client.GetMarshalableWithOptionalInterfacesAsync())?.As<IMarshalableSubType1>();
        Assert.Equal(1, await proxy1!.GetAsync(1));
        Assert.Equal(2, await proxy1.GetPlusOneAsync(1));
        this.AssertIsNot(proxy1, typeof(IMarshalableSubType2));
        this.AssertIsNot(proxy1, typeof(IMarshalableSubType2Extended));
    }

    [Fact]
    public async Task RpcMarshalableOptionalInterface_JsonRpcMethodAttribute()
    {
        this.server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableWithOptionalInterfaces();
        IMarshalableWithOptionalInterfaces? proxy = await this.client.GetMarshalableWithOptionalInterfacesAsync();
        Assert.Equal("foo", await proxy!.ToBeRenamedAsync("foo"));

        Assert.Equal("foo", await this.clientRpc.InvokeAsync<string>("$/invokeProxy/0/RenamedAsync", "foo"));

        this.server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableSubType1();
        IMarshalableWithOptionalInterfaces? proxy1 = await this.client.GetMarshalableWithOptionalInterfacesAsync();
        Assert.Equal("foo", await proxy1!.ToBeRenamedAsync("foo"));
        Assert.Equal("foo", await proxy1!.As<IMarshalableSubType1>()!.ToBeRenamedAsync("foo"));

        Assert.Equal("foo", await this.clientRpc.InvokeAsync<string>("$/invokeProxy/1/RenamedAsync", "foo"));
        Assert.Equal("foo", await this.clientRpc.InvokeAsync<string>("$/invokeProxy/1/1.RenamedAsync", "foo"));
    }

    [Fact]
    public async Task RpcMarshalableOptionalInterface_MethodNameTransform_Prefix()
    {
        var server = new Server();
        server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableSubType1();

        var streams = FullDuplexStream.CreatePair();
        var localRpc = JsonRpc.Attach(streams.Item2);
        var serverRpc = new JsonRpc(streams.Item1, streams.Item1);

        serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Verbose);
        localRpc.TraceSource = new TraceSource("Client", SourceLevels.Verbose);

        serverRpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions { MethodNameTransform = n => "one." + n });
        serverRpc.StartListening();

        var proxy = await localRpc.InvokeAsync<IMarshalableWithOptionalInterfaces>("one." + nameof(IServer.GetMarshalableWithOptionalInterfacesAsync));
        Assert.Equal(1, await proxy!.GetAsync(1));
        Assert.Equal(1, await proxy.As<IMarshalableSubType1>()!.GetAsync(1));
        Assert.Equal(2, await proxy.As<IMarshalableSubType1>()!.GetPlusOneAsync(1));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType2));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType2Extended));

        // The MethodNameTransform doesn't apply to the marshaled objects
        Assert.Equal(1, await localRpc.InvokeAsync<int>("$/invokeProxy/0/GetAsync", 1));
        Assert.Equal(1, await localRpc.InvokeAsync<int>("$/invokeProxy/0/1.GetAsync", 1));
    }

    [Fact]
    public async Task RpcMarshalableOptionalInterface_MethodNameTransform_CamelCase()
    {
        var server = new Server();
        server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableSubType1();

        var streams = FullDuplexStream.CreatePair();
        var localRpc = JsonRpc.Attach(streams.Item2);
        var serverRpc = new JsonRpc(streams.Item1, streams.Item1);

        serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Verbose);
        localRpc.TraceSource = new TraceSource("Client", SourceLevels.Verbose);

        serverRpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.CamelCase });
        serverRpc.StartListening();

        var proxy = await localRpc.InvokeAsync<IMarshalableWithOptionalInterfaces>("getMarshalableWithOptionalInterfacesAsync");
        Assert.Equal(1, await proxy!.GetAsync(1));
        Assert.Equal(1, await proxy!.As<IMarshalableSubType1>()!.GetAsync(1));
        Assert.Equal(2, await proxy!.As<IMarshalableSubType1>()!.GetPlusOneAsync(1));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType2));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType2Extended));

        // The MethodNameTransform doesn't apply to the marshaled objects
        Assert.Equal(1, await localRpc.InvokeAsync<int>("$/invokeProxy/0/GetAsync", 1));
        Assert.Equal(1, await localRpc.InvokeAsync<int>("$/invokeProxy/0/1.GetAsync", 1));
    }

    [Fact]
    public async Task RpcMarshalableOptionalInterface_Null()
    {
        this.server.ReturnedMarshalableWithOptionalInterfaces = null;
        IMarshalableWithOptionalInterfaces? proxy = await this.client.GetMarshalableWithOptionalInterfacesAsync();
        Assert.Null(proxy);
    }

    [Fact]
    public async Task RpcMarshalableOptionalInterface_IndirectInterfaceImplementation()
    {
        this.server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableSubType1Indirect();
        IMarshalableSubType1? proxy = (await this.client.GetMarshalableWithOptionalInterfacesAsync())?.As<IMarshalableSubType1>();
        Assert.Equal(1, await proxy!.GetAsync(1));
        Assert.Equal(2, await proxy.GetPlusOneAsync(1));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType2));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType2Extended));
    }

    [Fact]
    public async Task RpcMarshalableOptionalInterface_WithExplicitImplementation()
    {
        this.server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableSubType2();
        IMarshalableSubType2? proxy = (await this.client.GetMarshalableWithOptionalInterfacesAsync())?.As<IMarshalableSubType2>();
        Assert.Equal(1, await proxy!.GetAsync(1));
        Assert.Equal(3, await proxy.GetPlusTwoAsync(1));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType1));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType2Extended));
    }

    [Fact]
    public async Task RpcMarshalableOptionalInterface_UnknownSubType()
    {
        this.server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableUnknownSubType();
        IMarshalableWithOptionalInterfaces? proxy = await this.client.GetMarshalableWithOptionalInterfacesAsync();
        Assert.Equal(1, await proxy!.GetAsync(1));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType1));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType2));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType2Extended));
    }

    [Fact]
    public async Task RpcMarshalableOptionalInterface_OnlyAttibutesOnDeclaredTypeAreHonored()
    {
        this.server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableSubType2Extended();
        IMarshalableSubType2? proxy = (await this.client.GetMarshalableWithOptionalInterfacesAsync())?.As<IMarshalableSubType2>();
        Assert.Equal(1, await proxy!.GetAsync(1));
        Assert.Equal(3, await proxy.GetPlusTwoAsync(1));
        this.AssertIsNot(proxy, typeof(IMarshalableSubType2Extended));

        IMarshalableSubType2? proxy1 = await this.client.GetMarshalableSubType2Async();
        Assert.Equal(1, await proxy1!.GetAsync(1));
        Assert.Equal(3, await proxy1.GetPlusTwoAsync(1));
        Assert.Equal(4, await proxy1.As<IMarshalableSubType2Extended>()!.GetPlusThreeAsync(1));
    }

    [Fact]
    public async Task RpcMarshalableOptionalInterface_OptionalInterfaceNotExtendingBase()
    {
        this.server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableNonExtendingBase();
        IMarshalableWithOptionalInterfaces? proxy = await this.client.GetMarshalableWithOptionalInterfacesAsync();
        Assert.Equal(1, await proxy!.GetAsync(1));

        Assert.Equal(5, await proxy!.As<IMarshalableNonExtendingBase>()!.GetPlusFourAsync(1));
    }

    [Fact]
    public async Task RpcMarshalableOptionalInterface_IntermediateNonMarshalableInterface()
    {
        this.server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableSubTypeWithIntermediateInterface();
        IMarshalableWithOptionalInterfaces? proxy = await this.client.GetMarshalableWithOptionalInterfacesAsync();
        Assert.Equal(1, await proxy!.GetAsync(1));

        Assert.Equal(1, await ((IMarshalableSubTypeIntermediateInterface)proxy).GetAsync(1));
        Assert.Equal(2, await ((IMarshalableSubTypeIntermediateInterface)proxy).GetPlusOneAsync(1));

        // This should return 3, since MarshalableSubTypeWithIntermediateInterface implements this method explicitly
        // but StreamJsonRpc doesn't know about the IMarshalableSubTypeIntermediateInterface because it is not part
        // of the RPC contract, so IMarshalableSubTypeWithIntermediateInterface.GetPlusTwoAsync is invoked instead.
        Assert.Equal(-3, await ((IMarshalableSubTypeIntermediateInterface)proxy).GetPlusTwoAsync(1));

        Assert.Equal(1, await proxy.As<IMarshalableSubTypeWithIntermediateInterface>()!.GetAsync(1));
        Assert.Equal(2, await proxy.As<IMarshalableSubTypeWithIntermediateInterface>()!.GetPlusOneAsync(1));
        Assert.Equal(-3, await proxy.As<IMarshalableSubTypeWithIntermediateInterface>()!.GetPlusTwoAsync(1)); // This method negates the result
        Assert.Equal(4, await proxy.As<IMarshalableSubTypeWithIntermediateInterface>()!.GetPlusThreeAsync(1));
    }

    [Fact]
    public async Task RpcMarshalableOptionalInterface_MultipleIntermediateInterfaces()
    {
        this.server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableSubTypeWithIntermediateInterface1And2();
        IMarshalableWithOptionalInterfaces? proxy1 = await this.client.GetMarshalableWithOptionalInterfacesAsync();
        IMarshalableWithOptionalInterfaces2? proxy2 = await this.client.GetMarshalableWithOptionalInterfaces2Async();

        Assert.Equal(3, await proxy1!.As<IMarshalableSubTypeWithIntermediateInterface>()!.GetPlusTwoAsync(1));
        Assert.Equal(-3, await proxy1!.As<IMarshalableSubTypeWithIntermediateInterface2>()!.GetPlusTwoAsync(1));

        Assert.Equal(3, await proxy2!.As<IMarshalableSubTypeWithIntermediateInterface>()!.GetPlusTwoAsync(1));
        Assert.Equal(-3, await proxy2!.As<IMarshalableSubTypeWithIntermediateInterface2>()!.GetPlusTwoAsync(1));

        // Since MarshalableSubTypeWithIntermediateInterface1And2 implements the GetPlusTwoAsync methods explicitly
        // and IMarshalableSubTypeIntermediateInterface is not a known optional interface, a call to
        // IMarshalableSubTypeIntermediateInterface.GetPlusTwoAsync is dispatched to an undefined method:
        // either IMarshalableSubTypeWithIntermediateInterface.GetPlusTwoAsync or
        // either IMarshalableSubTypeWithIntermediateInterface2.GetPlusTwoAsync.
        // While the behavior is undefined, we don't want it to change over time: whatever the behavior is, we want it
        // to be consistent. So this test covers this arbitrary behavior to avoid it being changed in the future.
        // IMarshalableWithOptionalInterfaces and IMarshalableWithOptionalInterfaces2 have opposite
        // RpcMarshalableOptionalInterface definitions (the order of the optionalInterfaceCode values is inverted)
        // resulting in inverted dispatching.
        Assert.Equal(3, await proxy1!.As<IMarshalableSubTypeIntermediateInterface>()!.GetPlusTwoAsync(1));
        Assert.Equal(-3, await proxy2!.As<IMarshalableSubTypeIntermediateInterface>()!.GetPlusTwoAsync(1));
    }

    [Fact]
    public async Task RpcMarshalableOptionalInterface_MultipleImplementations()
    {
        this.server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableSubTypeMultipleImplementations();
        IMarshalableWithOptionalInterfaces? proxy = await this.client.GetMarshalableWithOptionalInterfacesAsync();
        Assert.Equal(1, await proxy!.GetAsync(1));

        Assert.Equal(5, await proxy.As<IMarshalableNonExtendingBase>()!.GetPlusFourAsync(1));

        Assert.Equal(1, await proxy.As<IMarshalableSubType1>()!.GetAsync(1));
        Assert.Equal(2, await proxy.As<IMarshalableSubType1>()!.GetPlusOneAsync(1));
        Assert.Equal(1, await proxy.As<IMarshalableSubType1>()!.GetMinusOneAsync(2));

        Assert.Equal(1, await proxy.As<IMarshalableSubType1Extended>()!.GetAsync(1));
        Assert.Equal(-2, await proxy.As<IMarshalableSubType1Extended>()!.GetPlusOneAsync(1)); // This method negates the result
        Assert.Equal(-1, await proxy.As<IMarshalableSubType1Extended>()!.GetMinusOneAsync(2)); // This method negates the result
        Assert.Equal(-3, await proxy.As<IMarshalableSubType1Extended>()!.GetPlusTwoAsync(1)); // This method negates the result
        Assert.Equal(4, await proxy.As<IMarshalableSubType1Extended>()!.GetPlusThreeAsync(1));
        Assert.Equal(-1, await proxy.As<IMarshalableSubType1Extended>()!.GetMinusTwoAsync(1));

        Assert.Equal(1, await proxy.As<IMarshalableSubType2>()!.GetAsync(1));
        Assert.Equal(3, await proxy.As<IMarshalableSubType2>()!.GetPlusTwoAsync(1));
        Assert.Equal(-1, await proxy.As<IMarshalableSubType2>()!.GetMinusTwoAsync(1));
    }

    [Fact]
    public async Task RpcMarshalableOptionalInterface_MultipleImplementationsCombined()
    {
        this.server.ReturnedMarshalableWithOptionalInterfaces = new MarshalableSubTypesCombined();
        IMarshalableWithOptionalInterfaces? proxy = await this.client.GetMarshalableWithOptionalInterfacesAsync();
        Assert.Equal(1, await proxy!.GetAsync(1));

        Assert.Equal(5, await proxy.As<IMarshalableNonExtendingBase>()!.GetPlusFourAsync(1));

        Assert.Equal(1, await proxy.As<IMarshalableSubType1>()!.GetAsync(1));
        Assert.Equal(2, await proxy.As<IMarshalableSubType1>()!.GetPlusOneAsync(1));
        Assert.Equal(1, await proxy.As<IMarshalableSubType1>()!.GetMinusOneAsync(2));

        Assert.Equal(1, await proxy.As<IMarshalableSubType1Extended>()!.GetAsync(1));
        Assert.Equal(-2, await proxy.As<IMarshalableSubType1Extended>()!.GetPlusOneAsync(1)); // This method negates the result
        Assert.Equal(-1, await proxy.As<IMarshalableSubType1Extended>()!.GetMinusOneAsync(2)); // This method negates the result
        Assert.Equal(-3, await proxy.As<IMarshalableSubType1Extended>()!.GetPlusTwoAsync(1)); // This method negates the result
        Assert.Equal(4, await proxy.As<IMarshalableSubType1Extended>()!.GetPlusThreeAsync(1));
        Assert.Equal(-1, await proxy.As<IMarshalableSubType1Extended>()!.GetMinusTwoAsync(1));

        Assert.Equal(1, await proxy.As<IMarshalableSubType2>()!.GetAsync(1));
        Assert.Equal(3, await proxy.As<IMarshalableSubType2>()!.GetPlusTwoAsync(1));
        Assert.Equal(-1, await proxy.As<IMarshalableSubType2>()!.GetMinusTwoAsync(1));

        Assert.Equal(1, await proxy.As<IMarshalableSubTypesCombined>()!.GetAsync(1));
        Assert.Equal(-2, await proxy.As<IMarshalableSubTypesCombined>()!.GetPlusOneAsync(1)); // This method negates the result
        Assert.Equal(-1, await proxy.As<IMarshalableSubTypesCombined>()!.GetMinusOneAsync(2)); // This method negates the result
        Assert.Equal(4, await proxy.As<IMarshalableSubTypesCombined>()!.GetPlusThreeAsync(1));
        Assert.Equal(6, await proxy.As<IMarshalableSubTypesCombined>()!.GetPlusFiveAsync(1));
    }

    [Fact]
    public async Task RpcMarshalable_CallScopedLifetime()
    {
        MarshalableAndSerializable marshaled = new();
        await this.client.CallScopedMarshalableAsync(marshaled);
        Assert.True(marshaled.DoSomethingCalled);
        Assert.False(marshaled.IsDisposed);
        this.clientRpc.Dispose();
        Assert.False(marshaled.IsDisposed);
    }

    [Fact]
    public async Task RpcMarshalable_CallScopedLifetime_AsyncEnumerableReturned()
    {
        MarshalableAndSerializable marshaled = new();
        await foreach (int number in this.client.CallScopedMarshalableReturnsAsyncEnumerable(marshaled))
        {
            Assert.Equal(42, number);
            Assert.True(marshaled.DoSomethingCalled);
        }

        // Verify that after enumeration conclusion, the proxy is zombied.
        this.server.AllowContinuation.Set();
        Assert.NotNull(this.server.ContinuationResult);
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.server.ContinuationResult).WithCancellation(this.TimeoutToken);
    }

    [Fact]
    public async Task RpcMarshalable_CallScopedLifetime_AsyncEnumerableThrown()
    {
        this.clientRpc.AllowModificationWhileListening = true;
        this.serverRpc.AllowModificationWhileListening = true;
        this.clientRpc.ExceptionStrategy = ExceptionProcessing.ISerializable;
        this.serverRpc.ExceptionStrategy = ExceptionProcessing.ISerializable;
        this.clientRpc.LoadableTypes.Add(typeof(ExceptionWithAsyncEnumerable));

        MarshalableAndSerializable marshaled = new();
        var outerException = await Assert.ThrowsAsync<RemoteInvocationException>(() => this.client.CallScopedMarshalableThrowsWithAsyncEnumerable(marshaled));
        var ex = Assert.IsType<ExceptionWithAsyncEnumerable>(outerException.InnerException);

        // Verify that the proxy is zombied immediately because the request failed.
        // Successfully enumerating is sufficient for this because the enumerator method has the assertion of zombie inside it.
        Assert.NotNull(ex.Enumerable);
        await foreach (int number in ex.Enumerable)
        {
            Assert.Equal(42, number);
        }
    }

    [Fact]
    public async Task RpcMarshalable_CallScopedLifetime_InvokedAfterReturn()
    {
        MarshalableAndSerializable marshaled = new();
        await this.client.CallScopedMarshalableAsync(marshaled);
        this.server.AllowContinuation.Set();
        Assert.NotNull(this.server.ContinuationResult);
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => this.server.ContinuationResult).WithCancellation(this.TimeoutToken);
    }

    [Fact]
    [Trait("GC", "")]
    public async Task RpcMarshalable_CallScopedLifetime_ObjectCollected()
    {
        WeakReference weakRef = await HelperAsync(this.client);
        await Task.Yield(); // get off the helper's inline continuation stack.
        AssertCollectedObject(weakRef);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static async Task<WeakReference> HelperAsync(IServer client)
        {
            MarshalableAndSerializable? marshaled = new();
            await client.CallScopedMarshalableAsync(marshaled);
            WeakReference result = new(marshaled);
            marshaled = null;
            return result;
        }
    }

    [Fact]
    public async Task RpcMarshalable_CallScopedLifetime_ObjectReturned()
    {
        var ex = await Assert.ThrowsAsync<RemoteSerializationException>(this.client.ReturnCallScopedObjectAsync);
        this.Logger.WriteLine(ex.ToString());
    }

    protected abstract IJsonRpcMessageFormatter CreateFormatter();

    private void AssertIsNot(object obj, Type type)
    {
        Assert.False(((IJsonRpcClientProxy)obj).Is(type), $"Object of type {obj.GetType().FullName} is not expected to be assignable to {type.FullName}");
        if (this is not (MarshalableProxyNerdbankMessagePackTests or MarshalableProxyPolyTypeJsonTests))
        {
            Assert.False(type.IsAssignableFrom(obj.GetType()), $"Object of type {obj.GetType().FullName} is not expected to be assignable to {type.FullName}");
        }
    }

    public class Server : IServer
    {
        internal AsyncAutoResetEvent AllowContinuation { get; } = new();

        internal AsyncManualResetEvent ReturnedMarshalableDisposed { get; } = new AsyncManualResetEvent();

        internal IMarshalable? ReturnedMarshalable { get; set; }

        internal IMarshalable? ReceivedProxy { get; set; }

        internal IMarshalableWithOptionalInterfaces? ReturnedMarshalableWithOptionalInterfaces { get; set; }

        internal Task? ContinuationResult { get; private set; }

        public Task<IMarshalable?> GetMarshalableAsync(bool returnNull)
        {
            // The OneObjectMarshalledTwiceHasIndependentLifetimes test depends on us returning the same instance each time.
            var marshalable = returnNull ? null : (this.ReturnedMarshalable ?? new Data(() => this.ReturnedMarshalableDisposed.Set()));
            this.ReturnedMarshalable = marshalable;
            return Task.FromResult(marshalable);
        }

        public Task<IMarshalableWithOptionalInterfaces?> GetMarshalableWithOptionalInterfacesAsync()
        {
            return Task.FromResult(this.ReturnedMarshalableWithOptionalInterfaces);
        }

        public Task<IMarshalableWithOptionalInterfaces2?> GetMarshalableWithOptionalInterfaces2Async()
        {
            return Task.FromResult(this.ReturnedMarshalableWithOptionalInterfaces?.As<IMarshalableWithOptionalInterfaces2>());
        }

        public Task<IMarshalableSubType2?> GetMarshalableSubType2Async()
        {
            return Task.FromResult(this.ReturnedMarshalableWithOptionalInterfaces?.As<IMarshalableSubType2>());
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

        public async Task CallScopedMarshalableAsync(IMarshalableWithCallScopedLifetime marshalable)
        {
            await marshalable.DoSomethingAsync();

            this.ContinuationResult = Task.Run(async delegate
            {
                await this.AllowContinuation.WaitAsync();
                await marshalable.DoSomethingAsync();
            });
        }

        public Task<IMarshalableWithCallScopedLifetime> ReturnCallScopedObjectAsync()
        {
            // Returning a call-scoped object as a return type is illegal.
            // This method is used in a test that verifies the failure mode for this case.
            return Task.FromResult<IMarshalableWithCallScopedLifetime>(new MarshalableAndSerializable());
        }

        public async IAsyncEnumerable<int> CallScopedMarshalableReturnsAsyncEnumerable(IMarshalableWithCallScopedLifetime marshalable)
        {
            // Yield before using the marshalable since we want to test that the call-scoped argument
            // is available for the whole async enumerable state machine.
            await Task.Yield();

            await marshalable.DoSomethingAsync();
            yield return 42;

            // This allows a test to optionally verify that the call-scoped object
            // quits working after the enumeration has completed.
            this.ContinuationResult = Task.Run(async delegate
            {
                await this.AllowContinuation.WaitAsync();
                await marshalable.DoSomethingAsync();
            });
        }

        public Task CallScopedMarshalableThrowsWithAsyncEnumerable(IMarshalableWithCallScopedLifetime marshalable)
        {
            throw new ExceptionWithAsyncEnumerable(Helper());

            async IAsyncEnumerable<int> Helper()
            {
                await Task.Yield();

                // By the time this runs, the original request has failed and using the call-scoped argument is expected to fail too.
                await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => marshalable.DoSomethingAsync());

                yield return 42;
            }
        }
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

        [PropertyShape(Ignore = true)]
        public bool IsDisposed { get; private set; }

        [PropertyShape(Ignore = true)]
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

    public class MarshalableAndSerializable : IMarshalableAndSerializable, IMarshalableWithCallScopedLifetime
    {
        public bool DoSomethingCalled { get; private set; }

        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            this.IsDisposed = true;
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

    public class MarshalableWithOptionalInterfaces : IMarshalableWithOptionalInterfaces
    {
        public Task<int> GetAsync(int value) => Task.FromResult(value);

        public Task<string> ToBeRenamedAsync(string value) => Task.FromResult(value);

        public void Dispose()
        {
        }
    }

    public class MarshalableSubType1 : IMarshalableSubType1
    {
        public Task<int> GetAsync(int value) => Task.FromResult(value);

        public Task<int> GetPlusOneAsync(int value) => Task.FromResult(value + 1);

        public Task<int> GetMinusOneAsync(int value) => Task.FromResult(value - 1);

        public Task<string> ToBeRenamedAsync(string value) => Task.FromResult(value);

        public void Dispose()
        {
        }
    }

    public class MarshalableSubType1Indirect : MarshalableSubType1
    {
    }

    public class MarshalableSubType2 : IMarshalableSubType2
    {
        Task<int> IMarshalableWithOptionalInterfaces.GetAsync(int value) => Task.FromResult(value);

        Task<int> IMarshalableSubType2.GetPlusTwoAsync(int value) => Task.FromResult(value + 2);

        public Task<int> GetMinusTwoAsync(int value) => Task.FromResult(value - 2);

        public Task<string> ToBeRenamedAsync(string value) => Task.FromResult(value);

        public void Dispose()
        {
        }
    }

    public class MarshalableSubType2Extended : IMarshalableSubType2Extended
    {
        public Task<int> GetAsync(int value) => Task.FromResult(value);

        public Task<int> GetPlusTwoAsync(int value) => Task.FromResult(value + 2);

        public Task<int> GetPlusThreeAsync(int value) => Task.FromResult(value + 3);

        public Task<int> GetMinusTwoAsync(int value) => Task.FromResult(value - 2);

        public Task<string> ToBeRenamedAsync(string value) => Task.FromResult(value);

        public void Dispose()
        {
        }
    }

    public class MarshalableUnknownSubType : IMarshalableUnknownSubType
    {
        public Task<int> GetAsync(int value) => Task.FromResult(value);

        public Task<string> ToBeRenamedAsync(string value) => Task.FromResult(value);

        public void Dispose()
        {
        }
    }

    public class MarshalableNonExtendingBase : IMarshalableWithOptionalInterfaces, IMarshalableNonExtendingBase
    {
        public Task<int> GetAsync(int value) => Task.FromResult(value);

        public Task<int> GetPlusFourAsync(int value) => Task.FromResult(value + 4);

        public Task<string> ToBeRenamedAsync(string value) => Task.FromResult(value);

        public void Dispose()
        {
        }
    }

    public class MarshalableSubTypeWithIntermediateInterface : IMarshalableSubTypeWithIntermediateInterface
    {
        public Task<int> GetAsync(int value) => Task.FromResult(value);

        public Task<int> GetPlusOneAsync(int value) => Task.FromResult(value + 1);

        public Task<int> GetPlusTwoAsync(int value) => Task.FromResult(-value - 2);

        Task<int> IMarshalableSubTypeIntermediateInterface.GetPlusTwoAsync(int value) => Task.FromResult(value + 2);

        public Task<int> GetPlusThreeAsync(int value) => Task.FromResult(value + 3);

        public Task<string> ToBeRenamedAsync(string value) => Task.FromResult(value);

        public void Dispose()
        {
        }
    }

    public class MarshalableSubTypeWithIntermediateInterface1And2 : IMarshalableSubTypeWithIntermediateInterface, IMarshalableSubTypeWithIntermediateInterface2
    {
        Task<int> IMarshalableSubTypeWithIntermediateInterface.GetPlusTwoAsync(int value) => Task.FromResult(value + 2);

        Task<int> IMarshalableSubTypeWithIntermediateInterface2.GetPlusTwoAsync(int value) => Task.FromResult(-value - 2);

        Task<int> IMarshalableSubTypeIntermediateInterface.GetPlusTwoAsync(int value) => throw new NotImplementedException();

        public Task<int> GetAsync(int value) => throw new NotImplementedException();

        public Task<int> GetPlusOneAsync(int value) => throw new NotImplementedException();

        public Task<int> GetPlusThreeAsync(int value) => throw new NotImplementedException();

        public Task<string> ToBeRenamedAsync(string value) => Task.FromResult(value);

        public void Dispose()
        {
        }
    }

    public class MarshalableSubTypeMultipleImplementations : IMarshalableSubType1Extended, IMarshalableSubType2, IMarshalableNonExtendingBase
    {
        public Task<int> GetAsync(int value) => Task.FromResult(value); // From both IMarshalableWithOptionalInterfaces and IMarshalableSubType1Extended (new keyword)

        public Task<int> GetPlusOneAsync(int value) => Task.FromResult(value + 1); // From IMarshalableSubType1

        Task<int> IMarshalableSubType1.GetMinusOneAsync(int value) => Task.FromResult(value - 1);

        Task<int> IMarshalableSubType2.GetPlusTwoAsync(int value) => Task.FromResult(value + 2);

        public Task<int> GetPlusThreeAsync(int value) => Task.FromResult(value + 3); // From IMarshalableSubType1Extended

        Task<int> IMarshalableSubType1Extended.GetPlusOneAsync(int value) => Task.FromResult(-value - 1);

        public Task<int> GetPlusTwoAsync(int value) => Task.FromResult(-value - 2); // From IMarshalableSubType1Extended

        Task<int> IMarshalableSubType1Extended.GetMinusOneAsync(int value) => Task.FromResult(-value + 1);

        public Task<int> GetMinusTwoAsync(int value) => Task.FromResult(value - 2); // From both IMarshalableSubType2 and IMarshalableSubType1Extended

        public Task<int> GetPlusFourAsync(int value) => Task.FromResult(value + 4); // From IMarshalableNonExtendingBase

        public Task<string> ToBeRenamedAsync(string value) => Task.FromResult(value);

        public void Dispose()
        {
        }
    }

    public class MarshalableSubTypesCombined : IMarshalableSubTypesCombined
    {
        public Task<int> GetAsync(int value) => Task.FromResult(value); // From both IMarshalableWithOptionalInterfaces and IMarshalableSubType1Extended (new keyword)

        public Task<int> GetPlusOneAsync(int value) => Task.FromResult(value + 1); // From IMarshalableSubType1

        Task<int> IMarshalableSubType1.GetMinusOneAsync(int value) => Task.FromResult(value - 1);

        Task<int> IMarshalableSubType2.GetPlusTwoAsync(int value) => Task.FromResult(value + 2);

        public Task<int> GetPlusThreeAsync(int value) => Task.FromResult(value + 3); // From IMarshalableSubType1Extended

        Task<int> IMarshalableSubType1Extended.GetPlusOneAsync(int value) => Task.FromResult(-value - 1);

        public Task<int> GetPlusTwoAsync(int value) => Task.FromResult(-value - 2); // From IMarshalableSubType1Extended

        Task<int> IMarshalableSubType1Extended.GetMinusOneAsync(int value) => Task.FromResult(-value + 1);

        public Task<int> GetMinusTwoAsync(int value) => Task.FromResult(value - 2); // From both IMarshalableSubType2 and IMarshalableSubType1Extended

        public Task<int> GetPlusFourAsync(int value) => Task.FromResult(value + 4); // From IMarshalableNonExtendingBase

        public Task<int> GetPlusFiveAsync(int value) => Task.FromResult(value + 5); // From IMarshalableSubTypesCombined

        public Task<string> ToBeRenamedAsync(string value) => Task.FromResult(value);

        public void Dispose()
        {
        }
    }

    [Serializable]
    public class ExceptionWithAsyncEnumerable : Exception
    {
        public ExceptionWithAsyncEnumerable(IAsyncEnumerable<int> enumeration)
        {
            this.Enumerable = enumeration;
        }

#if NET8_0_OR_GREATER
        [Obsolete]
#endif
        protected ExceptionWithAsyncEnumerable(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.Enumerable = (IAsyncEnumerable<int>?)info.GetValue(nameof(this.Enumerable), typeof(IAsyncEnumerable<int>));
        }

        internal IAsyncEnumerable<int>? Enumerable { get; set; }

#if NET8_0_OR_GREATER
        [Obsolete]
#endif
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue(nameof(this.Enumerable), this.Enumerable);
        }
    }
}
