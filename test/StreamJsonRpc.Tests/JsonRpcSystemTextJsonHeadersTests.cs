﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.Threading;

public partial class JsonRpcSystemTextJsonHeadersTests : JsonRpcTests
{
    public JsonRpcSystemTextJsonHeadersTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override Type FormatterExceptionType => typeof(JsonException);

    [Fact]
    public override async Task CanPassExceptionFromServer_ErrorData()
    {
        RemoteInvocationException exception = await Assert.ThrowsAnyAsync<RemoteInvocationException>(() => this.clientRpc.InvokeAsync(nameof(Server.MethodThatThrowsUnauthorizedAccessException)));
        Assert.Equal((int)JsonRpcErrorCode.InvocationError, exception.ErrorCode);

        var errorData = Assert.IsType<CommonErrorData>(exception.ErrorData);
        Assert.NotNull(errorData.StackTrace);
        Assert.StrictEqual(COR_E_UNAUTHORIZEDACCESS, errorData.HResult);
    }

    protected override void InitializeFormattersAndHandlers(
        Stream serverStream,
        Stream clientStream,
        out IJsonRpcMessageFormatter serverMessageFormatter,
        out IJsonRpcMessageFormatter clientMessageFormatter,
        out IJsonRpcMessageHandler serverMessageHandler,
        out IJsonRpcMessageHandler clientMessageHandler,
        bool controlledFlushingClient)
    {
        clientMessageFormatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions =
            {
                Converters =
                {
                    new TypeThrowsWhenDeserializedConverter(),
                },
                TypeInfoResolver = SourceGenerationContext.Default,
            },
        };
        serverMessageFormatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions =
            {
                Converters =
                {
                    new TypeThrowsWhenDeserializedConverter(),
                },
                TypeInfoResolver = SourceGenerationContext.Default,
            },
        };

        serverMessageHandler = new HeaderDelimitedMessageHandler(serverStream, serverStream, serverMessageFormatter);
        clientMessageHandler = controlledFlushingClient
            ? new DelayedFlushingHandler(clientStream, clientMessageFormatter)
            : new HeaderDelimitedMessageHandler(clientStream, clientStream, clientMessageFormatter);
    }

    protected override object[] CreateFormatterIntrinsicParamsObject(string arg) =>
    [
        new JsonObject { ["arg"] = JsonValue.Create(arg) },
        JsonDocument.Parse($$"""{ "arg": "{{arg}}" }""").RootElement, // JsonElement
    ];

    protected class DelayedFlushingHandler : HeaderDelimitedMessageHandler, IControlledFlushHandler
    {
        public DelayedFlushingHandler(Stream stream, IJsonRpcMessageFormatter formatter)
            : base(stream, formatter)
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

    private class TypeThrowsWhenDeserializedConverter : JsonConverter<TypeThrowsWhenDeserialized>
    {
        public override TypeThrowsWhenDeserialized? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw CreateExceptionToBeThrownByDeserializer();
        }

        public override void Write(Utf8JsonWriter writer, TypeThrowsWhenDeserialized value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }

    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(Exception))]
    private partial class SourceGenerationContext : JsonSerializerContext;
}
