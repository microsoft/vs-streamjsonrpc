#pragma warning disable SA1402 // File may only contain a single type

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.Serialization;
using System.Text;
using MessagePack;
using Nerdbank.Streams;
using StreamJsonRpc.Reflection;

public abstract class FormatterTestBase : TestBase
{
    protected FormatterTestBase(ITestOutputHelper logger)
        : base(logger)
    {
    }

    [DataContract]
    public class CustomType
    {
        [DataMember]
        public int Age { get; set; }
    }
}

public abstract class FormatterTestBase<TFormatter> : FormatterTestBase
    where TFormatter : IJsonRpcMessageFormatter
{
    private TFormatter? formatter;

    protected FormatterTestBase(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected TFormatter Formatter
    {
        get => this.formatter ??= this.CreateFormatter();
        set => this.formatter = value;
    }

    [Fact]
    public void TopLevelPropertiesCanBeSerializedRequest()
    {
        IJsonRpcMessageFactory? factory = this.CreateFormatter() as IJsonRpcMessageFactory;
        Assert.SkipWhen(factory is null, "No factory");
        JsonRpcRequest requestMessage = factory.CreateRequestMessage();
        Assert.NotNull(requestMessage);

        requestMessage.Method = "test";
        Assert.True(requestMessage.TrySetTopLevelProperty("testProperty", "testValue"));
        Assert.True(requestMessage.TrySetTopLevelProperty("objectProperty", new CustomType() { Age = 25 }));

        JsonRpcRequest roundTripMessage = this.Roundtrip(requestMessage);
        Assert.True(roundTripMessage.TryGetTopLevelProperty("testProperty", out string? value));
        Assert.Equal("testValue", value);

        Assert.True(roundTripMessage.TryGetTopLevelProperty("objectProperty", out CustomType? customObject));
        Assert.Equal(25, customObject?.Age);
    }

    [Fact]
    public void TopLevelPropertiesCanBeSerializedResult()
    {
        IJsonRpcMessageFactory? factory = this.CreateFormatter() as IJsonRpcMessageFactory;
        Assert.SkipWhen(factory is null, "No factory");
        var message = factory.CreateResultMessage();
        Assert.NotNull(message);

        message.Result = "test";
        Assert.True(message.TrySetTopLevelProperty("testProperty", "testValue"));
        Assert.True(message.TrySetTopLevelProperty("objectProperty", new CustomType() { Age = 25 }));

        var roundTripMessage = this.Roundtrip(message);
        Assert.True(roundTripMessage.TryGetTopLevelProperty("testProperty", out string? value));
        Assert.Equal("testValue", value);

        Assert.True(roundTripMessage.TryGetTopLevelProperty("objectProperty", out CustomType? customObject));
        Assert.Equal(25, customObject?.Age);
    }

    [Fact]
    public void TopLevelPropertiesCanBeSerializedError()
    {
        IJsonRpcMessageFactory? factory = this.CreateFormatter() as IJsonRpcMessageFactory;
        Assert.SkipWhen(factory is null, "No factory");
        var message = factory.CreateErrorMessage();
        Assert.NotNull(message);

        message.Error = new JsonRpcError.ErrorDetail() { Message = "test" };
        Assert.True(message.TrySetTopLevelProperty("testProperty", "testValue"));
        Assert.True(message.TrySetTopLevelProperty("objectProperty", new CustomType() { Age = 25 }));

        var roundTripMessage = this.Roundtrip(message);
        Assert.True(roundTripMessage.TryGetTopLevelProperty("testProperty", out string? value));
        Assert.Equal("testValue", value);

        Assert.True(roundTripMessage.TryGetTopLevelProperty("objectProperty", out CustomType? customObject));
        Assert.Equal(25, customObject?.Age);
    }

    [Fact]
    public void TopLevelPropertiesWithNullValue()
    {
        IJsonRpcMessageFactory? factory = this.CreateFormatter() as IJsonRpcMessageFactory;
        Assert.SkipWhen(factory is null, "No factory");
        var requestMessage = factory.CreateRequestMessage();
        Assert.NotNull(requestMessage);

        requestMessage.Method = "test";
        Assert.True(requestMessage.TrySetTopLevelProperty<string?>("testProperty", null));

        var roundTripMessage = this.Roundtrip(requestMessage);
        Assert.True(roundTripMessage.TryGetTopLevelProperty("testProperty", out string? value));
        Assert.Null(value);
    }

    /// <summary>
    /// Verifies that an error response that omits the optional <c>data</c> member, as some non-.NET JSON-RPC implementations do,
    /// surfaces as a <see cref="RemoteInvocationException"/> and leaves the connection usable.
    /// </summary>
    [Theory, CombinatorialData]
    public async Task ErrorResponseWithoutData(ExceptionProcessing exceptionStrategy)
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        TFormatter clientFormatter = this.CreateFormatter();
        using JsonRpc clientRpc = new(new LengthHeaderMessageHandler(clientStream, clientStream, clientFormatter))
        {
            ExceptionStrategy = exceptionStrategy,
        };
        clientRpc.StartListening();

        // Hand-roll the server so that we control exactly which members appear in the response.
        bool isJson = clientFormatter is IJsonRpcMessageTextFormatter;
        LengthHeaderMessageHandler serverReader = new(null, serverStream, this.CreateFormatter());

        Task<string> failingCall = clientRpc.InvokeWithCancellationAsync<string>("Fail", cancellationToken: this.TimeoutToken);
        await WriteFrameAsync(serverStream, CreateResponse(isJson, await ReadRequestIdAsync(), error: true), this.TimeoutToken);

        RemoteInvocationException ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => failingCall);
        Assert.Equal((int)JsonRpcErrorCode.InvocationErrorWithException, ex.ErrorCode);
        Assert.Equal("Oops", ex.Message);
        Assert.Null(ex.ErrorData);

        // The connection must remain usable.
        Task<string> succeedingCall = clientRpc.InvokeWithCancellationAsync<string>("Succeed", cancellationToken: this.TimeoutToken);
        await WriteFrameAsync(serverStream, CreateResponse(isJson, await ReadRequestIdAsync(), error: false), this.TimeoutToken);
        Assert.Equal("pong", await succeedingCall);

        async Task<RequestId> ReadRequestIdAsync()
        {
            JsonRpcRequest request = (JsonRpcRequest)(await serverReader.ReadAsync(this.TimeoutToken))!;
            ((IJsonRpcMessageBufferManager)serverReader).DeserializationComplete(request);
            return request.RequestId;
        }
    }

    protected abstract TFormatter CreateFormatter();

    protected T Roundtrip<T>(T value)
        where T : JsonRpcMessage
    {
        var sequence = new Sequence<byte>();
        this.Formatter.Serialize(sequence, value);
        var actual = (T)this.Formatter.Deserialize(sequence);
        return actual;
    }

    private static byte[] CreateResponse(bool json, RequestId requestId, bool error)
    {
        long id = requestId.Number ?? throw new NotSupportedException("Only numeric request IDs are supported.");
        const int code = (int)JsonRpcErrorCode.InvocationErrorWithException;
        if (json)
        {
            string body = error
                ? $$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":{{{code}}},"message":"Oops"}}"""
                : $$"""{"jsonrpc":"2.0","id":{{id}},"result":"pong"}""";
            return Encoding.UTF8.GetBytes(body);
        }

        using Sequence<byte> sequence = new();
        MessagePackWriter writer = new(sequence);
        writer.WriteMapHeader(3);
        writer.Write("jsonrpc");
        writer.Write("2.0");
        writer.Write("id");
        writer.Write(id);
        if (error)
        {
            writer.Write("error");
            writer.WriteMapHeader(2);
            writer.Write("code");
            writer.Write(code);
            writer.Write("message");
            writer.Write("Oops");
        }
        else
        {
            writer.Write("result");
            writer.Write("pong");
        }

        writer.Flush();
        return sequence.AsReadOnlySequence.ToArray();
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));
        await stream.WriteAsync(frame, 0, frame.Length, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
