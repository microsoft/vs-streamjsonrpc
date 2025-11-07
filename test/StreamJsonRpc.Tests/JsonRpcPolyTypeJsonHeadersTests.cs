// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable PolyTypeJson

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.Threading;

public partial class JsonRpcPolyTypeJsonHeadersTests : JsonRpcTests
{
    public JsonRpcPolyTypeJsonHeadersTests(ITestOutputHelper logger)
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
        clientMessageFormatter = CreateFormatter();
        serverMessageFormatter = CreateFormatter();

        serverMessageHandler = new HeaderDelimitedMessageHandler(serverStream, serverStream, serverMessageFormatter);
        clientMessageHandler = controlledFlushingClient
            ? new DelayedFlushingHandler(clientStream, clientMessageFormatter)
            : new HeaderDelimitedMessageHandler(clientStream, clientStream, clientMessageFormatter);

        static PolyTypeJsonFormatter CreateFormatter()
        {
            PolyTypeJsonFormatter formatter = new()
            {
                JsonSerializerOptions =
                {
                    TypeInfoResolver = SourceGenerationContext.Default,
                },
                TypeShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_StreamJsonRpc_Tests.Default,
            };
            formatter.RegisterGenericType<int>();
            formatter.RegisterGenericType<CustomSerializedType>();
            formatter.RegisterGenericType<TypeThrowsWhenSerialized>();
            return formatter;
        }
    }

    protected override object[] CreateFormatterIntrinsicParamsObject(string arg) =>
    [
        new JsonObject { ["arg"] = JsonValue.Create(arg) },
        JsonDocument.Parse($$"""{ "arg": "{{arg}}" }""").RootElement, // JsonElement
    ];

    public class TypeThrowsWhenDeserializedConverter : JsonConverter<TypeThrowsWhenDeserialized>
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

    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(int?))]
    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(double))]
    [JsonSerializable(typeof(Guid))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(JsonObject))]
    [JsonSerializable(typeof(Exception))]
    [JsonSerializable(typeof(ArgumentOutOfRangeException))]
    [JsonSerializable(typeof(PrivateSerializableException))]
    [JsonSerializable(typeof(JsonException))]
    [JsonSerializable(typeof(FileNotFoundException))]
    [JsonSerializable(typeof(Foo))]
    [JsonSerializable(typeof(Server.CustomErrorData))]
    [JsonSerializable(typeof(ExceptionMissingDeserializingConstructor))]
    [JsonSerializable(typeof(TypeThrowsWhenSerialized))]
    [JsonSerializable(typeof(TypeThrowsWhenDeserialized))]
    [JsonSerializable(typeof(InvalidOperationException))]
    [JsonSerializable(typeof(VAndWProperties))]
    [JsonSerializable(typeof(XAndYProperties))]
    [JsonSerializable(typeof(XAndYPropertiesWithProgress))]
    [JsonSerializable(typeof(ParamsObjectWithCustomNames))]
    [JsonSerializable(typeof(InternalClass))]
    [JsonSerializable(typeof(StrongTypedProgressType))]
    [JsonSerializable(typeof(ProgressWithCompletion<CustomSerializedType>))]
    [JsonSerializable(typeof(CustomSerializedType))]
    [JsonSerializable(typeof(IProgress<CustomSerializedType>))]
    [JsonSerializable(typeof(IProgress<TypeThrowsWhenSerialized>))]
    private partial class SourceGenerationContext : JsonSerializerContext;

    [GenerateShapeFor<JsonException>]
    [GenerateShapeFor<FileNotFoundException>]
    [GenerateShapeFor<ApplicationException>]
    [GenerateShapeFor<InvalidOperationException>]
    [GenerateShapeFor<ArgumentOutOfRangeException>]
    [GenerateShapeFor<InternalClass>]
    [GenerateShapeFor<JsonObject>]
    [GenerateShapeFor<JsonElement>]
    [GenerateShapeFor<double>]
    private partial class Witness;
}
