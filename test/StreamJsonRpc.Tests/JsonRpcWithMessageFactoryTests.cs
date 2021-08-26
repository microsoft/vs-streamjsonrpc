using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;
using Xunit.Abstractions;

public class JsonRpcWithMessageFactoryTests : TestBase
{
    private readonly Server server;
    private readonly IJsonRpcMessageHandler clientMessageHandler;
    private readonly IJsonRpcMessageHandler serverMessageHandler;
    private readonly MessageFormatterWithMessageFactory clientMessageFormatter;
    private readonly MessageFormatterWithMessageFactory serverMessageFormatter;
    private readonly JsonRpc clientRpc;
    private readonly JsonRpc serverRpc;

    public JsonRpcWithMessageFactoryTests(ITestOutputHelper logger)
        : base(logger)
    {
        this.server = new Server();
        var streams = Nerdbank.FullDuplexStream.CreateStreams();

        this.clientMessageFormatter = new MessageFormatterWithMessageFactory();
        this.clientMessageHandler = new HeaderDelimitedMessageHandler(streams.Item1, streams.Item1, this.clientMessageFormatter);

        this.serverMessageFormatter = new MessageFormatterWithMessageFactory();
        this.serverMessageHandler = new HeaderDelimitedMessageHandler(streams.Item2, streams.Item2, this.serverMessageFormatter);

        this.clientRpc = new JsonRpc(this.clientMessageHandler);
        this.serverRpc = new JsonRpc(this.serverMessageHandler, this.server);

        this.serverRpc.TraceSource = new TraceSource("Server", SourceLevels.Information);
        this.clientRpc.TraceSource = new TraceSource("Client", SourceLevels.Information);

        this.serverRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));
        this.clientRpc.TraceSource.Listeners.Add(new XunitTraceListener(this.Logger));

        this.clientRpc.StartListening();
        this.serverRpc.StartListening();
    }

    [Fact]
    public async Task RequestMessageIsCreatedFromClientFactory()
    {
        await this.clientRpc.InvokeAsync<string>(nameof(Server.TestMethodAsync));
        Assert.True(this.clientMessageFormatter.IsCreateRequestMessageCalled);
        Assert.False(this.serverMessageFormatter.IsCreateRequestMessageCalled);
    }

    [Fact]
    public async Task ResultMessageIsCreatedFromServerFactory()
    {
        await this.clientRpc.InvokeAsync<string>(nameof(Server.TestMethodAsync));
        Assert.False(this.clientMessageFormatter.IsCreateResultMessageCalled);
        Assert.True(this.serverMessageFormatter.IsCreateResultMessageCalled);
    }

    [Fact]
    public async Task ErrorMessageIsCreatedFromServerFactory()
    {
        await Assert.ThrowsAsync<RemoteInvocationException>(async () => await this.clientRpc.InvokeAsync<string>(nameof(Server.MethodThatThrowsAsync)));
        Assert.False(this.clientMessageFormatter.IsCreateErrorMessageCalled);
        Assert.False(this.serverMessageFormatter.IsCreateResultMessageCalled);
        Assert.True(this.serverMessageFormatter.IsCreateErrorMessageCalled);
    }

#pragma warning disable CA1801 // use all parameters
    public class Server
    {
        public Task TestMethodAsync()
        {
            return Task.CompletedTask;
        }

        public Task MethodThatThrowsAsync()
        {
            throw new InvalidProgramException();
        }
    }

    internal class MessageFormatterWithMessageFactory : IJsonRpcMessageTextFormatter, IJsonRpcMessageFactory
    {
        private readonly IJsonRpcMessageTextFormatter formatter = new JsonMessageFormatter();

        public bool IsCreateErrorMessageCalled { get; private set; }

        public bool IsCreateRequestMessageCalled { get; private set; }

        public bool IsCreateResultMessageCalled { get; private set; }

        public Encoding Encoding { get => this.formatter.Encoding; set => this.formatter.Encoding = value; }

        public JsonRpcError CreateErrorMessage()
        {
            this.IsCreateErrorMessageCalled = true;
            return new MyJsonRpcError();
        }

        public JsonRpcRequest CreateRequestMessage()
        {
            this.IsCreateRequestMessageCalled = true;
            return new MyJsonRpcRequest();
        }

        public JsonRpcResult CreateResultMessage()
        {
            this.IsCreateResultMessageCalled = true;
            return new MyJsonRpcResult();
        }

        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer)
        {
            throw new NotSupportedException();
        }

        public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer, Encoding encoding)
        {
            return this.formatter.Deserialize(contentBuffer, encoding);
        }

        public object GetJsonText(JsonRpcMessage message)
        {
            throw new NotSupportedException();
        }

        public void Serialize(IBufferWriter<byte> bufferWriter, JsonRpcMessage message)
        {
            this.formatter.Serialize(bufferWriter, message);
        }
    }

    internal class MyJsonRpcError : JsonRpcError
    {
    }

    internal class MyJsonRpcRequest : JsonRpcRequest
    {
    }

    internal class MyJsonRpcResult : JsonRpcResult
    {
    }
#pragma warning restore CA1801 // use all parameters
}
