﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using MessagePack;
using MessagePack.Formatters;
using Microsoft.VisualStudio.Threading;
using PolyType;

public abstract partial class JsonRpcMessagePackLengthTests(ITestOutputHelper logger) : JsonRpcTests(logger)
{
    [JsonRpcContract]
    internal partial interface IMessagePackServer
    {
        Task<UnionBaseClass> ReturnUnionTypeAsync(CancellationToken cancellationToken);

        Task<string?> AcceptUnionTypeAndReturnStringAsync(UnionBaseClass value, CancellationToken cancellationToken);

        Task AcceptUnionTypeAsync(UnionBaseClass value, CancellationToken cancellationToken);

        Task ProgressUnionType(IProgress<UnionBaseClass> progress, CancellationToken cancellationToken);

        IAsyncEnumerable<UnionBaseClass> GetAsyncEnumerableOfUnionType(CancellationToken cancellationToken);

        Task<bool> IsExtensionArgNonNull(CustomExtensionType extensionValue);
    }

    [Fact]
    public override async Task CanPassAndCallPrivateMethodsObjects()
    {
        var result = await this.clientRpc.InvokeAsync<Foo>(nameof(Server.MethodThatAcceptsFoo), new Foo { Bar = "bar", Bazz = 1000 });
        Assert.NotNull(result);
        Assert.Equal("bar!", result.Bar);
        Assert.Equal(1001, result.Bazz);
    }

    [Fact]
    public async Task ExceptionControllingErrorData()
    {
        var exception = await Assert.ThrowsAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync(nameof(Server.ThrowLocalRpcException))).WithCancellation(this.TimeoutToken);

        IDictionary<object, object>? data = (IDictionary<object, object>?)exception.ErrorData;
        Assert.NotNull(data);
        object myCustomData = data["MyCustomData"];
        string actual = (string)myCustomData;
        Assert.Equal("hi", actual);
    }

    [Fact]
    public override async Task CanPassExceptionFromServer_ErrorData()
    {
        RemoteInvocationException exception = await Assert.ThrowsAnyAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync(nameof(Server.MethodThatThrowsUnauthorizedAccessException)));
        Assert.Equal((int)JsonRpcErrorCode.InvocationError, exception.ErrorCode);

        var errorData = Assert.IsType<CommonErrorData>(exception.ErrorData);
        Assert.NotNull(errorData.StackTrace);
        Assert.StrictEqual(COR_E_UNAUTHORIZEDACCESS, errorData.HResult);
    }

    /// <summary>
    /// Verifies that return values can support union types by considering the return type as declared in the server method signature.
    /// </summary>
    [Fact]
    public async Task UnionType_ReturnValue()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(new MessagePackServer());
        UnionBaseClass result = await this.clientRpc.InvokeWithCancellationAsync<UnionBaseClass>(nameof(MessagePackServer.ReturnUnionTypeAsync), null, this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(result);
    }

    /// <summary>
    /// Verifies that return values can support union types by considering the return type as declared in the server method signature.
    /// </summary>
    [Fact]
    public async Task UnionType_ReturnValue_NonAsync()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(new MessagePackServer());
        UnionBaseClass result = await this.clientRpc.InvokeWithCancellationAsync<UnionBaseClass>(nameof(MessagePackServer.ReturnUnionType), null, this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(result);
    }

    /// <summary>
    /// Verifies that positional parameters can support union types by providing extra type information for each argument.
    /// </summary>
    [Theory]
    [CombinatorialData]
    public async Task UnionType_PositionalParameter_NoReturnValue(bool notify)
    {
        var server = new MessagePackServer();
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(server);
        UnionBaseClass? receivedValue;
        if (notify)
        {
            await this.clientRpc.NotifyAsync(nameof(MessagePackServer.AcceptUnionTypeAsync), new object?[] { new UnionDerivedClass() }, new[] { typeof(UnionBaseClass) }).WithCancellation(this.TimeoutToken);
            receivedValue = await server.ReceivedValueSource.Task.WithCancellation(this.TimeoutToken);
        }
        else
        {
            await this.clientRpc.InvokeWithCancellationAsync(nameof(MessagePackServer.AcceptUnionTypeAsync), new object?[] { new UnionDerivedClass() }, new[] { typeof(UnionBaseClass) }, this.TimeoutToken);
            receivedValue = server.ReceivedValue;
        }

        Assert.IsType<UnionDerivedClass>(receivedValue);
    }

    /// <summary>
    /// Verifies that positional parameters can support union types by providing extra type information for each argument.
    /// </summary>
    [Fact]
    public async Task UnionType_PositionalParameter_AndReturnValue()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(new MessagePackServer());
        string? result = await this.clientRpc.InvokeWithCancellationAsync<string?>(nameof(MessagePackServer.AcceptUnionTypeAndReturnStringAsync), new object?[] { new UnionDerivedClass() }, new[] { typeof(UnionBaseClass) }, this.TimeoutToken);
        Assert.Equal(typeof(UnionDerivedClass).Name, result);
    }

    /// <summary>
    /// Verifies that the type information associated with named parameters is used for proper serialization of union types.
    /// </summary>
    [Theory]
    [CombinatorialData]
    public async Task UnionType_NamedParameter_NoReturnValue_UntypedDictionary(bool notify)
    {
        var server = new MessagePackServer();
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(server);
        var argument = new Dictionary<string, object?> { { "value", new UnionDerivedClass() } };
        var argumentDeclaredTypes = new Dictionary<string, Type> { { "value", typeof(UnionBaseClass) } };

        UnionBaseClass? receivedValue;
        if (notify)
        {
            await this.clientRpc.NotifyWithParameterObjectAsync(nameof(MessagePackServer.AcceptUnionTypeAsync), argument, argumentDeclaredTypes).WithCancellation(this.TimeoutToken);
            receivedValue = await server.ReceivedValueSource.Task.WithCancellation(this.TimeoutToken);
        }
        else
        {
            await this.clientRpc.InvokeWithParameterObjectAsync(nameof(MessagePackServer.AcceptUnionTypeAsync), argument, argumentDeclaredTypes, this.TimeoutToken);
            receivedValue = server.ReceivedValue;
        }

        Assert.IsType<UnionDerivedClass>(receivedValue);

        // Exercise the non-init path by repeating
        server.ReceivedValueSource = new TaskCompletionSource<UnionBaseClass>();
        if (notify)
        {
            await this.clientRpc.NotifyWithParameterObjectAsync(nameof(MessagePackServer.AcceptUnionTypeAsync), argument, argumentDeclaredTypes).WithCancellation(this.TimeoutToken);
            receivedValue = await server.ReceivedValueSource.Task.WithCancellation(this.TimeoutToken);
        }
        else
        {
            await this.clientRpc.InvokeWithParameterObjectAsync(nameof(MessagePackServer.AcceptUnionTypeAsync), argument, argumentDeclaredTypes, this.TimeoutToken);
            receivedValue = server.ReceivedValue;
        }

        Assert.IsType<UnionDerivedClass>(receivedValue);
    }

    /// <summary>
    /// Verifies that the type information associated with named parameters is used for proper serialization of union types.
    /// </summary>
    [Fact]
    public async Task UnionType_NamedParameter_AndReturnValue_UntypedDictionary()
    {
        var server = new MessagePackServer();
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(server);
        string? result = await this.clientRpc.InvokeWithParameterObjectAsync<string?>(
            nameof(MessagePackServer.AcceptUnionTypeAndReturnStringAsync),
            new Dictionary<string, object?> { { "value", new UnionDerivedClass() } },
            new Dictionary<string, Type> { { "value", typeof(UnionBaseClass) } },
            this.TimeoutToken);
        Assert.Equal(typeof(UnionDerivedClass).Name, result);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);

        // Exercise the non-init path by repeating
        result = await this.clientRpc.InvokeWithParameterObjectAsync<string?>(
            nameof(MessagePackServer.AcceptUnionTypeAndReturnStringAsync),
            new Dictionary<string, object?> { { "value", new UnionDerivedClass() } },
            new Dictionary<string, Type> { { "value", typeof(UnionBaseClass) } },
            this.TimeoutToken);
        Assert.Equal(typeof(UnionDerivedClass).Name, result);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);
    }

    /// <summary>
    /// Verifies that the type information associated with named parameters is used for proper serialization of union types.
    /// </summary>
    [Theory]
    [CombinatorialData]
    public async Task UnionType_NamedParameter_NoReturnValue(bool notify)
    {
        var server = new MessagePackServer();
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(server);
        var namedArgs = NamedArgs.Create(new { value = (UnionBaseClass)new UnionDerivedClass() });

        UnionBaseClass? receivedValue;
        if (notify)
        {
            await this.clientRpc.NotifyWithParameterObjectAsync(nameof(MessagePackServer.AcceptUnionTypeAsync), namedArgs).WithCancellation(this.TimeoutToken);
            receivedValue = await server.ReceivedValueSource.Task.WithCancellation(this.TimeoutToken);
        }
        else
        {
            await this.clientRpc.InvokeWithParameterObjectAsync(nameof(MessagePackServer.AcceptUnionTypeAsync), namedArgs, this.TimeoutToken);
            receivedValue = server.ReceivedValue;
        }

        Assert.IsType<UnionDerivedClass>(receivedValue);

        // Exercise the non-init path by repeating
        server.ReceivedValueSource = new TaskCompletionSource<UnionBaseClass>();
        if (notify)
        {
            await this.clientRpc.NotifyWithParameterObjectAsync(nameof(MessagePackServer.AcceptUnionTypeAsync), namedArgs).WithCancellation(this.TimeoutToken);
            receivedValue = await server.ReceivedValueSource.Task.WithCancellation(this.TimeoutToken);
        }
        else
        {
            await this.clientRpc.InvokeWithParameterObjectAsync(nameof(MessagePackServer.AcceptUnionTypeAsync), namedArgs, this.TimeoutToken);
            receivedValue = server.ReceivedValue;
        }

        Assert.IsType<UnionDerivedClass>(receivedValue);
    }

    /// <summary>
    /// Verifies that the type information associated with named parameters is used for proper serialization of union types.
    /// </summary>
    [Fact]
    public async Task UnionType_NamedParameter_AndReturnValue()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(new MessagePackServer());
        string? result = await this.clientRpc.InvokeWithParameterObjectAsync<string?>(nameof(MessagePackServer.AcceptUnionTypeAndReturnStringAsync), NamedArgs.Create(new { value = (UnionBaseClass)new UnionDerivedClass() }), this.TimeoutToken);
        Assert.Equal(typeof(UnionDerivedClass).Name, result);
    }

    /// <summary>
    /// Verifies that return values can support union types by considering the return type as declared in the server method signature.
    /// </summary>
    [Fact]
    public async Task UnionType_ReturnValue_Proxy()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(new MessagePackServer());
        var clientProxy = this.clientRpc.Attach<IMessagePackServer>();
        UnionBaseClass result = await clientProxy.ReturnUnionTypeAsync(this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(result);
    }

    /// <summary>
    /// Verifies that positional parameters can support union types by providing extra type information for each argument.
    /// </summary>
    [Fact]
    public async Task UnionType_PositionalParameter_AndReturnValue_Proxy()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(new MessagePackServer());
        var clientProxy = this.clientRpc.Attach<IMessagePackServer>();
        string? result = await clientProxy.AcceptUnionTypeAndReturnStringAsync(new UnionDerivedClass(), this.TimeoutToken);
        Assert.Equal(typeof(UnionDerivedClass).Name, result);

        // Repeat the proxy call to exercise the non-init path of the dynamically generated proxy.
        result = await clientProxy.AcceptUnionTypeAndReturnStringAsync(new UnionDerivedClass(), this.TimeoutToken);
        Assert.Equal(typeof(UnionDerivedClass).Name, result);
    }

    /// <summary>
    /// Verifies that the type information associated with named parameters is used for proper serialization of union types.
    /// </summary>
    [Fact]
    public async Task UnionType_NamedParameter_AndReturnValue_Proxy()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(new MessagePackServer());
        var clientProxy = this.clientRpc.Attach<IMessagePackServer>(new JsonRpcProxyOptions { ServerRequiresNamedArguments = true });
        string? result = await clientProxy.AcceptUnionTypeAndReturnStringAsync(new UnionDerivedClass(), this.TimeoutToken);
        Assert.Equal(typeof(UnionDerivedClass).Name, result);

        // Repeat the proxy call to exercise the non-init path of the dynamically generated proxy.
        result = await clientProxy.AcceptUnionTypeAndReturnStringAsync(new UnionDerivedClass(), this.TimeoutToken);
        Assert.Equal(typeof(UnionDerivedClass).Name, result);
    }

    /// <summary>
    /// Verifies that positional parameters can support union types by providing extra type information for each argument.
    /// </summary>
    [Fact]
    public async Task UnionType_PositionalParameter_NoReturnValue_Proxy()
    {
        var server = new MessagePackServer();
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(server);
        var clientProxy = this.clientRpc.Attach<IMessagePackServer>();
        await clientProxy.AcceptUnionTypeAsync(new UnionDerivedClass(), this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);

        // Repeat the proxy call to exercise the non-init path of the dynamically generated proxy.
        server.ReceivedValueSource = new TaskCompletionSource<UnionBaseClass>();
        await clientProxy.AcceptUnionTypeAsync(new UnionDerivedClass(), this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);
    }

    /// <summary>
    /// Verifies that the type information associated with named parameters is used for proper serialization of union types.
    /// </summary>
    [Fact]
    public async Task UnionType_NamedParameter_NoReturnValue_Proxy()
    {
        var server = new MessagePackServer();
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(server);
        var clientProxy = this.clientRpc.Attach<IMessagePackServer>(new JsonRpcProxyOptions { ServerRequiresNamedArguments = true });
        await clientProxy.AcceptUnionTypeAsync(new UnionDerivedClass(), this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);

        // Repeat the proxy call to exercise the non-init path of the dynamically generated proxy.
        server.ReceivedValueSource = new TaskCompletionSource<UnionBaseClass>();
        await clientProxy.AcceptUnionTypeAsync(new UnionDerivedClass(), this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);
    }

    [Fact]
    public async Task UnionType_AsIProgressTypeArgument()
    {
        var server = new MessagePackServer();
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(server);
        var clientProxy = this.clientRpc.Attach<IMessagePackServer>();

        var reportSource = new TaskCompletionSource<UnionBaseClass>();
        var progress = new Progress<UnionBaseClass>(v => reportSource.SetResult(v));
        await clientProxy.ProgressUnionType(progress, this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(await reportSource.Task.WithCancellation(this.TimeoutToken));
    }

    [Fact]
    public async Task UnionType_AsAsyncEnumerableTypeArgument()
    {
        var server = new MessagePackServer();
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(server);
        var clientProxy = this.clientRpc.Attach<IMessagePackServer>();

        UnionBaseClass? actualItem = null;
        await foreach (UnionBaseClass item in clientProxy.GetAsyncEnumerableOfUnionType(this.TimeoutToken))
        {
            actualItem = item;
        }

        Assert.IsType<UnionDerivedClass>(actualItem);
    }

    /// <summary>
    /// Verifies that an argument that cannot be deserialized by the msgpack primitive formatter will not cause a failure.
    /// </summary>
    /// <remarks>
    /// <para>This is a regression test for <see href="https://github.com/microsoft/vs-streamjsonrpc/issues/841">a bug</see> where
    /// verbose ETW tracing would fail to deserialize arguments with the primitive formatter that deserialize just fine for the actual method dispatch.</para>
    /// <para>This test is effective because custom msgpack extensions cause the <see cref="PrimitiveObjectFormatter"/> to throw an exception when deserializing.</para>
    /// </remarks>
    [Theory, PairwiseData]
    public async Task VerboseLoggingDoesNotFailWhenArgsDoNotDeserializePrimitively(bool namedArguments)
    {
        Assert.SkipUnless(SharedUtilities.GetEventSourceTestMode() == SharedUtilities.EventSourceTestMode.EmulateProduction, $"This test specifically verifies behavior when the EventSource should swallow exceptions. Current mode: {SharedUtilities.GetEventSourceTestMode()}.");
        var server = new MessagePackServer();
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(server);
        var clientProxy = this.clientRpc.Attach<IMessagePackServer>(new JsonRpcProxyOptions { ServerRequiresNamedArguments = namedArguments });

        Assert.True(await clientProxy.IsExtensionArgNonNull(new CustomExtensionType()));
    }

    protected override object[] CreateFormatterIntrinsicParamsObject(string arg) => [];

    [MessagePackObject]
    [Union(0, typeof(UnionDerivedClass))]
    [GenerateShape]
    [DerivedTypeShape(typeof(UnionDerivedClass))]
    public abstract partial class UnionBaseClass
    {
    }

    [GenerateShape]
    [MessagePackObject]
    public partial class UnionDerivedClass : UnionBaseClass
    {
    }

    [GenerateShape]
    internal partial class CustomExtensionType
    {
    }

    internal class MessagePackServer : IMessagePackServer
    {
        internal UnionBaseClass? ReceivedValue { get; private set; }

        internal TaskCompletionSource<UnionBaseClass> ReceivedValueSource { get; set; } = new TaskCompletionSource<UnionBaseClass>();

        public Task<UnionBaseClass> ReturnUnionTypeAsync(CancellationToken cancellationToken) => Task.FromResult<UnionBaseClass>(new UnionDerivedClass());

        public Task<string?> AcceptUnionTypeAndReturnStringAsync(UnionBaseClass value, CancellationToken cancellationToken) => Task.FromResult((this.ReceivedValue = value)?.GetType().Name);

        public Task AcceptUnionTypeAsync(UnionBaseClass value, CancellationToken cancellationToken)
        {
            this.ReceivedValue = value;
            this.ReceivedValueSource.SetResult(value);
            return Task.CompletedTask;
        }

        public UnionBaseClass ReturnUnionType() => new UnionDerivedClass();

        public Task ProgressUnionType(IProgress<UnionBaseClass> progress, CancellationToken cancellationToken)
        {
            progress.Report(new UnionDerivedClass());
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<UnionBaseClass> GetAsyncEnumerableOfUnionType([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new UnionDerivedClass();
        }

        public Task<bool> IsExtensionArgNonNull(CustomExtensionType extensionValue) => Task.FromResult(extensionValue is not null);
    }

    protected class DelayedFlushingHandler : LengthHeaderMessageHandler, IControlledFlushHandler
    {
        public DelayedFlushingHandler(Stream stream, IJsonRpcMessageFormatter formatter)
            : base(stream, stream, formatter)
        {
        }

        public AsyncAutoResetEvent FlushEntered { get; } = new AsyncAutoResetEvent();

        public AsyncManualResetEvent AllowFlushAsyncExit { get; } = new AsyncManualResetEvent();

        protected override async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            this.FlushEntered.Set();
            await this.AllowFlushAsyncExit.WaitAsync(CancellationToken.None);
            await base.FlushAsync(cancellationToken);
        }
    }
}
