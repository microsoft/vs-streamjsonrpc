// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

    protected override void InitializeFormattersAndHandlers()
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
        this.clientMessageHandler = new LengthHeaderMessageHandler(this.clientStream, this.clientStream, this.clientMessageFormatter);
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
}
