// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcMessagePackLengthTests : JsonRpcTests
{
    public JsonRpcMessagePackLengthTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    internal interface IMessagePackServer
    {
        Task<UnionBaseClass> ReturnUnionTypeAsync(CancellationToken cancellationToken);

        Task<string?> AcceptUnionTypeAndReturnStringAsync(UnionBaseClass value, CancellationToken cancellationToken);

        Task AcceptUnionTypeAsync(UnionBaseClass value, CancellationToken cancellationToken);
    }

    [Fact]
    public async Task CanPassAndCallPrivateMethodsObjects()
    {
        var result = await this.clientRpc.InvokeAsync<Foo>(nameof(Server.MethodThatAcceptsFoo), new Foo { Bar = "bar", Bazz = 1000 });
        Assert.NotNull(result);
        Assert.Equal("bar!", result.Bar);
        Assert.Equal(1001, result.Bazz);
    }

    [Fact]
    public async Task ExceptionControllingErrorData()
    {
        var exception = await Assert.ThrowsAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync(nameof(Server.ThrowRemoteInvocationException))).WithCancellation(this.TimeoutToken);

        IDictionary<object, object>? data = (IDictionary<object, object>?)exception.ErrorData;
        object myCustomData = data!["myCustomData"];
        string actual = (string)myCustomData;
        Assert.Equal("hi", actual);
    }

    [Fact]
    public async Task CanPassExceptionFromServer_ErrorData()
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
    [Fact]
    public async Task UnionType_PositionalParameter_NoReturnValue()
    {
        var server = new MessagePackServer();
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(server);
        await this.clientRpc.InvokeWithCancellationAsync(nameof(MessagePackServer.AcceptUnionTypeAsync), new object?[] { new UnionDerivedClass() }, new[] { typeof(UnionBaseClass) }, this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);
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
    [Fact]
    public async Task UnionType_NamedParameter_NoReturnValue_UntypedDictionary()
    {
        var server = new MessagePackServer();
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(server);
        await this.clientRpc.InvokeWithParameterObjectAsync(
            nameof(MessagePackServer.AcceptUnionTypeAsync),
            new Dictionary<string, object> { { "value", new UnionDerivedClass() } },
            new Dictionary<string, Type> { { "value", typeof(UnionBaseClass) } },
            this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);

        // Exercise the non-init path by repeating
        await this.clientRpc.InvokeWithParameterObjectAsync(
            nameof(MessagePackServer.AcceptUnionTypeAsync),
            new Dictionary<string, object> { { "value", new UnionDerivedClass() } },
            new Dictionary<string, Type> { { "value", typeof(UnionBaseClass) } },
            this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);
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
            new Dictionary<string, object> { { "value", new UnionDerivedClass() } },
            new Dictionary<string, Type> { { "value", typeof(UnionBaseClass) } },
            this.TimeoutToken);
        Assert.Equal(typeof(UnionDerivedClass).Name, result);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);

        // Exercise the non-init path by repeating
        result = await this.clientRpc.InvokeWithParameterObjectAsync<string?>(
            nameof(MessagePackServer.AcceptUnionTypeAndReturnStringAsync),
            new Dictionary<string, object> { { "value", new UnionDerivedClass() } },
            new Dictionary<string, Type> { { "value", typeof(UnionBaseClass) } },
            this.TimeoutToken);
        Assert.Equal(typeof(UnionDerivedClass).Name, result);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);
    }

    /// <summary>
    /// Verifies that the type information associated with named parameters is used for proper serialization of union types.
    /// </summary>
    [Fact]
    public async Task UnionType_NamedParameter_NoReturnValue()
    {
        var server = new MessagePackServer();
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(server);
        await this.clientRpc.InvokeWithParameterObjectAsync(nameof(MessagePackServer.AcceptUnionTypeAsync), new { value = (UnionBaseClass)new UnionDerivedClass() }, this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);

        // Exercise the non-init path by repeating
        await this.clientRpc.InvokeWithParameterObjectAsync(nameof(MessagePackServer.AcceptUnionTypeAsync), new { value = (UnionBaseClass)new UnionDerivedClass() }, this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);
    }

    /// <summary>
    /// Verifies that the type information associated with named parameters is used for proper serialization of union types.
    /// </summary>
    [Fact]
    public async Task UnionType_NamedParameter_AndReturnValue()
    {
        this.serverRpc.AllowModificationWhileListening = true;
        this.serverRpc.AddLocalRpcTarget(new MessagePackServer());
        string? result = await this.clientRpc.InvokeWithParameterObjectAsync<string?>(nameof(MessagePackServer.AcceptUnionTypeAndReturnStringAsync), new { value = (UnionBaseClass)new UnionDerivedClass() }, this.TimeoutToken);
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
        await clientProxy.AcceptUnionTypeAsync(new UnionDerivedClass(), this.TimeoutToken);
        Assert.IsType<UnionDerivedClass>(server.ReceivedValue);
    }

    protected override void InitializeFormattersAndHandlers(bool controlledFlushingClient)
    {
        this.serverMessageFormatter = new MessagePackFormatter();
        this.clientMessageFormatter = new MessagePackFormatter();

        var options = MessagePackSerializerOptions.Standard
            .WithResolver(CompositeResolver.Create(
                new IMessagePackFormatter[] { new UnserializableTypeFormatter(), new TypeThrowsWhenDeserializedFormatter() },
                new IFormatterResolver[] { StandardResolverAllowPrivate.Instance }));
        ((MessagePackFormatter)this.serverMessageFormatter).SetMessagePackSerializerOptions(options);
        ((MessagePackFormatter)this.clientMessageFormatter).SetMessagePackSerializerOptions(options);

        this.serverMessageHandler = new LengthHeaderMessageHandler(this.serverStream, this.serverStream, this.serverMessageFormatter);
        this.clientMessageHandler = controlledFlushingClient
            ? new DelayedFlushingHandler(this.clientStream, this.clientMessageFormatter)
            : new LengthHeaderMessageHandler(this.clientStream, this.clientStream, this.clientMessageFormatter);
    }

    [MessagePackObject]
    [Union(0, typeof(UnionDerivedClass))]
    public abstract class UnionBaseClass
    {
    }

    [MessagePackObject]
    public class UnionDerivedClass : UnionBaseClass
    {
    }

    private class UnserializableTypeFormatter : IMessagePackFormatter<CustomSerializedType>
    {
        public CustomSerializedType Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            return new CustomSerializedType { Value = reader.ReadString() };
        }

        public void Serialize(ref MessagePackWriter writer, CustomSerializedType value, MessagePackSerializerOptions options)
        {
            writer.Write(value?.Value);
        }
    }

    private class TypeThrowsWhenDeserializedFormatter : IMessagePackFormatter<TypeThrowsWhenDeserialized>
    {
        public TypeThrowsWhenDeserialized Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            throw CreateExceptionToBeThrownByDeserializer();
        }

        public void Serialize(ref MessagePackWriter writer, TypeThrowsWhenDeserialized value, MessagePackSerializerOptions options)
        {
            writer.WriteArrayHeader(0);
        }
    }

    private class MessagePackServer : IMessagePackServer
    {
        internal UnionBaseClass? ReceivedValue { get; private set; }

        public Task<UnionBaseClass> ReturnUnionTypeAsync(CancellationToken cancellationToken) => Task.FromResult<UnionBaseClass>(new UnionDerivedClass());

        public Task<string?> AcceptUnionTypeAndReturnStringAsync(UnionBaseClass value, CancellationToken cancellationToken) => Task.FromResult((this.ReceivedValue = value)?.GetType().Name);

        public Task AcceptUnionTypeAsync(UnionBaseClass value, CancellationToken cancellationToken)
        {
            this.ReceivedValue = value;
            return Task.CompletedTask;
        }

        public UnionBaseClass ReturnUnionType() => new UnionDerivedClass();
    }

    private class DelayedFlushingHandler : LengthHeaderMessageHandler, IControlledFlushHandler
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
            await this.AllowFlushAsyncExit.WaitAsync();
            await base.FlushAsync(cancellationToken);
        }
    }
}
